# 共享数据契约（data-contracts）

## 0. 类型与消费者

类型：数据 schema + 异步通知事件。**门槛口径：数据契约 ≥2 消费者方入本文件；事件允许单消费者，随提供模块规格托管，此处统一列出。**

| 契约 | 提供者 | 消费者 |
|---|---|---|
| `ProcessSnapshot` / `SignalSet` / `SignalFailure` / `ScanResult` | scanner | rules、releaser、ui |
| `MemoryOverview` | scanner | releaser（释放前后采样）、ui（呈现） |
| `Classification` / `QueryResult` | rules | ui |
| `ClassificationContext` | rules（入参契约，编排方构造） | rules、ui（预标高亮：**ui 消费 `RequiresElevation`，不重算预标谓词**） |
| `ReleaseRequest` / `TreePlan` / `ReleaseItemResult` / `ReleaseReport` | releaser | ui、storage |
| `WhitelistEntry` / `WhitelistSnapshot` / `RulePack` | storage | rules（注入）、releaser（注入）、ui |
| 事件：`TreeProgress` / `ReleaseCompleted` / `ScanFailed` | 各提供模块 | ui（+App 编排） |

## 1. 契约定义

### 1.1 扫描域（scanner 提供）

- **`ProcessSnapshot`**：`Pid`（int，**唯一键，违约定=扫描失败**）、`ParentPid`、`Name`、`ExecutablePath`（可空=受保护/系统/不可读，不可读须伴随 Unreadable SignalFailure）、`CreationTimeUtc`（**Kind 须为 Utc**；身份校验与 PID 复用判定的采集侧输入；不可读=MinValue 哨兵，§2 T-01 裁决）、`PrivateCommittedBytes`（long）、`CommandLine`（可空，WMI 通道；null=不可读，通道级失败经 SignalFailure 登记不可行——v1 接受仅字段级 null 表达）、`OwnerUser`（可空=不可读；跨用户预标依据；格式=裸用户名，§2 T-01 裁决）、`Signals`（SignalSet，1..1）。单位一律字节（PRD 的 MB 为展示换算）。
- **`SignalSet`**（字段级；**仅采集型信号**，名单匹配类判定归 rules 派生）：

  | 字段 | 类型/枚举 | 对应口径# |
  |---|---|---|
  | OrphanHint | enum{ParentDead, PidReused, No} | #1 |
  | SameDirAlivePids | IReadOnlySet\<int\>（**不含目标自身**；孤儿群互斥计算归 rules；路径归一化在采集侧完成） | #3 |
  | HasVisibleWindow | bool?（null=枚举失败） | #4 |
  | IsUwpPackage | bool | #4 |
  | TcpEstablishedCount | int | #6 |
  | CpuDeltaSeconds | double? | #7 |
  | ServiceName | string?（null=非服务） | #8 |
  | ServiceRestartOnFailure | bool? | #8 |
  | SignatureStatus | enum{Microsoft, ValidNonMicrosoft, Invalid, Unsigned, NotCollected, Unverifiable} | #9 |
  | SignerName | string?（ValidNonMicrosoft 时必填；安全软件第二通道） | #11 |
  | IsSystemDirectory | bool? | #10 |
  | SourceEntries | IReadOnlyList\<SourceEntry{Type: enum{RunKey,Service,ScheduledTask,StartupFolder}, EntryName}\> | #12 |
  | ScheduledTaskWouldRevive | bool?（触发器/动作匹配） | #15 |

  （#2 残留模式库、#5 常驻、#11 安全软件、#13 阈值、#14 白名单为 rules 派生判定，无采集字段。）
  **配对不变量（强制）**：某口径采集失败时，除字段自身 null 语义外，必须同时登记对应 SignalFailure——rules 以 Failures 为保守兜底的事实源，字段默认值（false/0/空集）不构成「已核实」证据。
- **`SignalFailure`**：`SignalId`（口径表编号；基础字段失败另有 100–103 编号段与 0 保留值，见 §2 T-01 裁决③）、`Pid`（int?，null=采集器级全局失败，非单进程）、`Kind`（enum{AccessDenied=打开受拒/PPL，Unreadable=部分元数据不可读，CollectorFailed=采集器/通道失败}）、`Detail`（人读原因，不作判定输入）。**保守兜底按 Kind+Pid 精确承载**（system 法-3）：AccessDenied→🚫受保护；Unreadable/CollectorFailed→相关进程不进✅。全局失败（Pid=null）v1 语义=全量进程不进✅（全有全无；代价已接受：采集器级失败意味着扫描数据整体不可信）。
- **`ClassificationContext`**：`SelfPid`（本工具自身 PID，🚫"本工具自身"判定依据）、`CurrentUserName`（string?，当前用户，跨用户预标依据，T-07 消费；null=获取失败→跨用户预标不可判，兜底方向归 T-07 裁决）。编排方构造后注入 rules，保持判定纯函数（不含环境读取）。
- **`ScanResult`**：`TakenAtUtc`、`ProcessCount`（=Snapshots.Count 的冗余快照，语义=尝试枚举总数；v1 与 Snapshots 一致）、`DurationMs`、`Snapshots`、`Failures`。不可变（构造后调用方不得变更底层集合，实现以防御性拷贝保证）。
- **`MemoryOverview`**：`PhysicalTotalBytes`、`InUseBytes`、`CommitBytes`、`CommitLimitBytes`、`StandbyBytes`（可空=降级）、`Source`（enum{NtQuery, Pdh, Degraded}）。

### 1.2 判定域（rules 提供）

- **`Classification`**：`Pid`、`Level`（enum{Recommend, Caution, Protected, Unmatched, Whitelisted}；**代码枚举序 Unmatched=0** 为防御性默认）、`Bases`（有序 `Basis{SignalId, Detail}`；SignalId=口径表编号，**0=保留值**：非口径表依据[采集失败兜底/本工具自身/系统保护穷举名/名单不可用/跨用户预标]，口径表无对应行）、`TreePrivateBytes`（long，树合计唯一承载）、`WouldBeRevived`（bool?，null=不适用/未评估）、`SourceEntries`、`RequiresElevation`（bool，预标：服务[口径#8]与受拒[口径#11]由 T-06 Classify 输出；跨用户预标归 T-07，含依据入 Bases）。
- **`QueryResult`**（T-07 交付）：`Pid`+`Name`（=契约的 Target，被定位进程）、`Outcome`（复用 `Level` 枚举：Recommend/Caution/Protected=各级；Unmatched=未命中规则不进列表；Whitelisted=白名单排除——不另设第二套分级枚举。v1 引擎结构上不产生 Unmatched——每进程至少命中一条依据，此通道为防御性保留）、`Bases`（=对应 Classification 的依据，逐字段一致）。`Query` 按 Pid 或进程名（OrdinalIgnoreCase）定位，同名多进程全部返回（每进程一个 `QueryResult`，调用方得到 `IReadOnlyList<QueryResult>`）；Pid 与名同时给出时 Pid 优先（名参数忽略）；两者均空 → 空集；classifications 缺项的进程不返回。Query 消费编排方持有的 Classify 全量输出（不重算判定，判定单一事实源=Classify）。

### 1.3 释放域（releaser 提供）

- **`ReleaseRequest`**：`ReleaseId`（Guid，幂等标识）、`SnapshotRef`、`SelectedPids`、`RequestedAtUtc`。
- **`TreePlan`**：`Id`（=RootPid）、`RootPid`、`Nodes`、`SkippedNodes`（节点+原因）、`TreePrivateBytes`（确认弹窗与释放量数据源）。
- **`ReleaseItemResult`**：`Pid`、`Name`、`ExecutablePath`、`CommandLine`、`Outcome`（**8 值**：Released=优雅关闭成功 / ForceKilled=强制结束 / Exited=已自行退出 / IdentityChanged=进程已变化跳过 / NeedsElevation=需管理员 / Blocked=被拦截 / SkippedProtected / SkippedWhitelisted）、`Reason`、`ErrorCode`。
- **`ReleaseReport`**：`ReleaseId`、`RequestedAtUtc`、`StartedAtUtc`、`FinishedAtUtc`、`Before`/`After`（MemoryOverview）、`Items`、`MainReleasedBytes`、`CheckReleasedBytes`（可负）、`LogPersisted`（bool?，日志写结果，null=未尝试）。落盘形态 = [PRD F6 JSONL](../../PRD.md)（时区/字段映射归 storage 落盘层）。

### 1.4 持久化域（storage 提供）

- **`WhitelistEntry`**：`Name`（匹配键）、`AddedAtUtc`、`Path`（可空，记录性）、`Note`（可空）。
- **`WhitelistSnapshot`**：不可变 `WhitelistEntry` 只读集（一次扫描一个一致视图）。名称匹配语义 = OrdinalIgnoreCase（v1 已知边界：同名不同路径一并排除）；**`ContainsName` 谓词为全系统唯一白名单匹配点**（rules/releaser 一律复用，禁止第二处实现）。
- **`RulePack`**：`ResidualPatterns[]`（子串）、`ResidentApps[]`（精确名）、`SecurityApps[]`（名+签名方；Signer 可空=null 仅按名称通道匹配）、`ProtectedProcesses[]`（穷举名）。内容基线引用 [PRD 附录名单清单](../../PRD.md)；**加载失败经编排方传入空包，空名单按"保护性依据缺失"兜底（v1 不区分真实空与失败，接受保守误降级）**。

### 1.5 事件契约（异步通知；同步结果走方法返回值）

- **线程亲和性（③.s4 grilling 裁决 2026-09-08）**：事件均从提供模块工作线程发出（releaser：`TreeProgress`/`ReleaseCompleted`；scanner：`ScanFailed`），提供方不做线程切换；ui（App 编排）负责编组（marshal）到 UI 线程（ui.md 既有法条同口径）。
- `TreeProgress(TreePlan.Id, TreeState)`：releaser → ui；树级粒度（`TreeState` 枚举随 T-09 契约化）。
- `ReleaseCompleted(ReleaseReport)`：releaser → ui；**至多一次**；App 编排据此调 `IReleaseLogStore.Append`（单路径，无直调并行路径；ReleaseId 为幂等标识）。
- `ScanFailed(Error)`：scanner → ui；触发 [PRD §3.7"扫描失败"行](../../PRD.md)（列表保留旧结果，丢弃部分结果）。

## 2. 版本与兼容

契约变更流程（system 法-5）：先改本文件 → 评审 → 再改实现。v1 文件格式不设版本字段（单机自用、可重建）；破坏性语义变更以 spec 变更记录 + 显式迁移声明承载。事件/数据字段可新增（新增=兼容），删除或语义反转=破坏性（须评审记录）。

> 2026-09-05（T-06 开工裁决）：① `SignalFailure` 结构化——加 `Pid`（精确兜底绑定，null=采集器级）与 `Kind` 枚举（判定引擎不可匹配自由文本；承载 GWT#12 PPL/🚫 与 #13 提权/⚠️ 的区分）；② 新增 `ClassificationContext`（SelfPid=「本工具自身」🚫判定依据；CurrentUserName 供 T-07 跨用户预标）——纯函数约束下环境信息一律参数注入；③ `ProcessSnapshot` 补 `Signals`（SignalSet，1..1）聚合关系澄清。实现于 T-06。
>
> 2026-09-07（T-07 开工裁决）：① `QueryResult` 定形——Target=(Pid,Name)、Outcome 复用 `Level` 枚举（Unmatched=未命中规则、Whitelisted=白名单排除）、Bases 与 Classification 逐字段一致；Query 消费编排方持有的 Classify 全量输出不重算（rules §4.1 既有口径）；同名多进程全返回，Pid 优先于名。② 跨用户预标 null 兜底方向（§1.1 预留裁决点）：`OwnerUser` 或 `CurrentUserName` 为 null（含获取失败）→ 不预标——预标语义=「已知需管理员」的正面标记，不可判≠已知；漏标风险由释放分类执行期兜底（PRD F3 步骤 7）。两侧均非 null 时按 OrdinalIgnoreCase 比较，不等 → 置 `RequiresElevation=true` 并附依据（SignalId=0 入 Bases）；预标不改级、不参与冲突消解。实现于 T-07。
>
> 2026-09-07（T-01 开工裁决）：① `OwnerUser` 格式口径（#27 裁决，用户选定）：采集侧归一**裸所有者名**（LookupAccountSid 账户名分量，不含域前缀），与编排侧 `CurrentUserName`（`Environment.UserName`，裸名）同格式，T-07 整串 OrdinalIgnoreCase 比较成立；跨域同名用户不可区分 → 漏预标，由释放分类执行期兜底（与 null 兜底同向）。② `CreationTimeUtc` 不可读表达：非空字段维持，不可读（打开被拒/查询失败）→ `DateTime.MinValue`（Kind=Utc）哨兵 + 配对 SignalFailure；哨兵不参与 PID 复用比较（任一方哨兵→不复用判定，失败记录兜底不进✅）。③ 基础字段失败编号段：`SignalId=100` 路径、`101` 创建时间、`102` 私有提交、`103` 所有者（scanner 采集侧专用，非口径表 1–15；`0` 仍为进程级保留值）；进程级打开失败（OpenProcess 被拒/进程已消失）单条记录 SignalId=0、Kind=AccessDenied（被拒）/Unreadable（消失），覆盖路径/创建时间/私有提交/所有者四字段，不逐字段重复记录；私有提交不可读值=0（口径#13 兜底）。④ 扫描中途退出进程保留于快照（时点口径），元数据不可读落 Unreadable。⑤ `CommandLine` WMI 通道 1.5s 超时：超时按通道级失败 → 全量字段级 null、无记录（本节 v1 既有口径）。实现于 T-01。

> 2026-09-08（T-05 开工裁决）：① PDH 内存通道走手写 P/Invoke（pdh.dll 薄通道，`ExcludeFromCodeCoverage`）——`PDH_FMT_COUNTERVALUE` 匿名联合在 CsWin32 `allowMarshaling=false` 下生成访问形态不稳，与 NtQuerySystemInformation 手写例外同类延伸（system-spec §4 例外清单已同步第④项）。② `MemoryOverviewSource` 语义：`NtQuery`/`Pdh`=standby 可得；`Degraded` ⇔ `StandbyBytes=null`（standby 不可得，AC 口径）。③ 通道梯为三级：NtQuery（含 standby）→ PDH（commit/available/standby）→ GlobalMemoryStatusEx 终底（规格双通道皆败的契约空档落点：commit 用页面文件口径近似——`CommitLimitBytes≈页面文件总量`、standby 恒 null、恒标 `Degraded`，ui 按 `Source=Degraded` 呈现降级）。④ 偏移实证：NtQuery 可用内存与 PDH/GMS 同刻对照 ≤10%、commit/standby 双通道互证（真机 2026-09-08）；开发中曾因漏算前缀 3×ULONG I/O 操作计数致错位（按 @32 取址），修正为实证布局 AvailablePages@0x2C/CommittedPages@0x30/CommitLimit@0x34（32/64 位同构，出处 Geoff Chappell SystemPerformanceInformation）。实现于 T-05。

> 2026-09-08（③.s4 grilling 补跑裁决，TL 逐条采纳）：① **提权重启失败项回传**：唯一通道=启动参数（PRD F3"状态不落盘"），内容基线=ui.md §27 既有定义（自动重扫标志+失败项清单[名称+可执行路径]），格式细则归 T-16 开工裁决、解析归 T-14；命令行 32K 上限为已知边界，超限降级为"不携带、仅自动重扫"（T-16 实现时登记）。② **CPU 差分窗口（口径 #7）**：窗口=TakeSnapshot 采集段内首尾两次采样（scanner 保持无状态），差分值写入 SignalSet #7 字段；T-02 开工重申。③ **事件线程亲和性**：见 §1.5。④ **PRD §3.3 手写例外枚举过期**：并入 issue #30 一次 PRD v1.4 小修收口。⑤ **ReleaseReport Before/After 时点**：Before=Execute 进入时（第一树启动前）、After=全部树终态后（含取消收尾），与 StartedAtUtc/FinishedAtUtc 对齐；T-10 开工重申。⑥ **加白即时性（F4）**：加白成功后即时重跑 Classify 并从推荐列表移除该行，F2 白名单排除计数同步；T-15 开工重申。⑦ **releaser 窗口发现**：释放决策用窗口句柄由 releaser 执行时自查（EnumWindows 按 PID 过滤取顶层可见窗口，复用口径 #4 的 DWM cloaked 过滤与 UWP 特例规则；自查失败 → 按有窗口走优雅路径兜底）；口径 #4 信号仅供判定，不供释放决策；T-09 开工重申。⑧ **启动行为**：正常启动不自动扫描（用户点击触发）；仅携带重启参数时自动扫；T-14 开工重申。⑨ **单实例互斥**：命名 Mutex（`Local\MemRelief.SingleInstance`），已启动则激活既有窗口后退出；提权重启链路让位规则——携带重启参数启动时对互斥做有限等待重试（或旧实例先释放互斥再拉起新实例），防提权新实例误判"已启动"静默退出；T-14 组合根实现。⑩ **T-20 测试程序对形态**：独立项目 `tests/MemRelief.TestProcs/`（WinExe），不进 T-19 单文件发布；T-20 开工重申。

> 2026-09-08（T-02 开工裁决）：① 手写 P/Invoke 通道扩展：窗口（EnumWindows/GetWindowThreadProcessId/GetWindowLong/DwmGetWindowAttribute）、TCP 表（GetExtendedTcpTable）、服务枚举（OpenSCManager 系）——WNDENUMPROC 回调委托与 DWMWA_CLOAKED 在 `allowMarshaling=false` 下 CsWin32 实测缺型，变长表结构生成访问形态不稳（system-spec §4 第④项已泛化）；窗口通道经 `[UnmanagedCallersOnly]`+GCHandle 承载回调状态。② CPU 差分窗口=TakeSnapshot 采集段（grilling 裁决②重申）：起点随枚举句柄捕获（GetProcessTimes 同调用近零成本），终点段内二次重开采样；不可得（窗口内退出/受拒/无采样）→ `CpuDeltaSeconds=null` + SignalFailure #7（口径兜底"不可读→不进✅"，rules 以 Failures 为事实源）。③ 同 pid 多服务（svchost 分组）：`ServiceName` 取首个，`ServiceRestartOnFailure`=任一配置重启动作即 true；QueryServiceConfig2 读取失败 → null + SignalFailure #8。④ `IsSystemDirectory=null` 仅表示路径不可得（编号 100 已覆盖，不重复 #10）；清单解析失败 → 全 null+全局 #10（rules 按保守视为系统目录）。⑤ OpenSCManager 走最小只读权限（CONNECT|ENUMERATE_SERVICE，ALL_ACCESS 普通权限 err=5 实证 2026-09-08），与"默认普通权限+按需提权"设计一致；枚举缓冲不足回 ERROR_MORE_DATA(234)（实测，非 122）。实现于 T-02。
>
> 2026-09-11（#33 裁决 a）：2026-09-08 ③.s4 段①⑧⑨ 的实现归属由 T-14 改派 **T-27**（新增 issue #34，T-14 AC 未含该三项、WBS 将自动重扫归 T-16，双源冲突经 TL 裁决收口）；① 格式细则裁决仍归 T-16 开工裁决，与 T-27 解析侧对接。

## 3. SLA / 非功能

分段预算（system §7 分解）：`TakeSnapshot` ≤2.0s、`CollectSignatures` ≤0.5s、`Classify` ≤0.3s（端到端 3s 含渲染，[PRD §3.4](../../PRD.md)）；`ReleaseCompleted` 端到端 ≤30s。
