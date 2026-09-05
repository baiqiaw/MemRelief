# 共享数据契约（data-contracts）

## 0. 类型与消费者

类型：数据 schema + 异步通知事件。**门槛口径：数据契约 ≥2 消费者方入本文件；事件允许单消费者，随提供模块规格托管，此处统一列出。**

| 契约 | 提供者 | 消费者 |
|---|---|---|
| `ProcessSnapshot` / `SignalSet` / `SignalFailure` / `ScanResult` | scanner | rules、releaser、ui |
| `MemoryOverview` | scanner | releaser（释放前后采样）、ui（呈现） |
| `Classification` / `QueryResult` | rules | ui |
| `ReleaseRequest` / `TreePlan` / `ReleaseItemResult` / `ReleaseReport` | releaser | ui、storage |
| `WhitelistEntry` / `WhitelistSnapshot` / `RulePack` | storage | rules（注入）、releaser（注入）、ui |
| 事件：`TreeProgress` / `ReleaseCompleted` / `ScanFailed` | 各提供模块 | ui（+App 编排） |

## 1. 契约定义

### 1.1 扫描域（scanner 提供）

- **`ProcessSnapshot`**：`Pid`（int，唯一键）、`ParentPid`、`Name`、`ExecutablePath`（可空=受保护/系统）、`CreationTimeUtc`（身份校验与 PID 复用判定）、`PrivateCommittedBytes`（long）、`CommandLine`（可空，WMI 通道）、`OwnerUser`（可空=不可读；跨用户预标依据）。单位一律字节（PRD 的 MB 为展示换算）。
- **`SignalSet`**（字段级；**仅采集型信号**，名单匹配类判定归 rules 派生）：

  | 字段 | 类型/枚举 | 对应口径# |
  |---|---|---|
  | OrphanHint | enum{ParentDead, PidReused, No} | #1 |
  | SameDirAlivePids | ISet\<int\>（孤儿群互斥计算归 rules） | #3 |
  | HasVisibleWindow | bool?（null=枚举失败） | #4 |
  | IsUwpPackage | bool | #4 |
  | TcpEstablishedCount | int | #6 |
  | CpuDeltaSeconds | double? | #7 |
  | ServiceName | string?（null=非服务） | #8 |
  | ServiceRestartOnFailure | bool? | #8 |
  | SignatureStatus | enum{Microsoft, ValidNonMicrosoft, Invalid, Unsigned, NotCollected, Unverifiable} | #9 |
  | SignerName | string?（ValidNonMicrosoft 时必填；安全软件第二通道） | #11 |
  | IsSystemDirectory | bool? | #10 |
  | SourceEntries | IList\<{Type: enum{RunKey,Service,ScheduledTask,StartupFolder}, EntryName}\> | #12 |
  | ScheduledTaskWouldRevive | bool?（触发器/动作匹配） | #15 |

  （#2 残留模式库、#5 常驻、#11 安全软件、#13 阈值、#14 白名单为 rules 派生判定，无采集字段。）
- **`SignalFailure`**：`SignalId`（口径表编号）、`Reason`。出现即触发保守兜底（system 法-3）。
- **`ScanResult`**：`TakenAtUtc`、`ProcessCount`、`DurationMs`、`Snapshots`、`Failures`。不可变。
- **`MemoryOverview`**：`PhysicalTotalBytes`、`InUseBytes`、`CommitBytes`、`CommitLimitBytes`、`StandbyBytes`（可空=降级）、`Source`（enum{NtQuery, Pdh, Degraded}）。

### 1.2 判定域（rules 提供）

- **`Classification`**：`Pid`、`Level`（enum{Recommend, Caution, Protected, Unmatched, Whitelisted}）、`Bases`（有序 `Basis{SignalId, Detail}`）、`TreePrivateBytes`（long，树合计唯一承载）、`WouldBeRevived`（bool?，null=不适用/未评估）、`SourceEntries`、`RequiresElevation`（bool，预标：服务[口径#8]或跨用户/受拒，含依据入 Bases）。
- **`QueryResult`**：`Target`、`Outcome`（各级/未命中规则/白名单排除）、`Bases`。

### 1.3 释放域（releaser 提供）

- **`ReleaseRequest`**：`ReleaseId`（Guid，幂等标识）、`SnapshotRef`、`SelectedPids`、`RequestedAtUtc`。
- **`TreePlan`**：`Id`（=RootPid）、`RootPid`、`Nodes`、`SkippedNodes`（节点+原因）、`TreePrivateBytes`（确认弹窗与释放量数据源）。
- **`ReleaseItemResult`**：`Pid`、`Name`、`ExecutablePath`、`CommandLine`、`Outcome`（**8 值**：Released=优雅关闭成功 / ForceKilled=强制结束 / Exited=已自行退出 / IdentityChanged=进程已变化跳过 / NeedsElevation=需管理员 / Blocked=被拦截 / SkippedProtected / SkippedWhitelisted）、`Reason`、`ErrorCode`。
- **`ReleaseReport`**：`ReleaseId`、`RequestedAtUtc`、`StartedAtUtc`、`FinishedAtUtc`、`Before`/`After`（MemoryOverview）、`Items`、`MainReleasedBytes`、`CheckReleasedBytes`（可负）、`LogPersisted`（bool?，日志写结果，null=未尝试）。落盘形态 = [PRD F6 JSONL](../../PRD.md)（时区/字段映射归 storage 落盘层）。

### 1.4 持久化域（storage 提供）

- **`WhitelistEntry`**：`Name`（匹配键）、`AddedAtUtc`、`Path`（记录性）、`Note`。
- **`WhitelistSnapshot`**：不可变 `WhitelistEntry` 只读集（一次扫描一个一致视图）。
- **`RulePack`**：`ResidualPatterns[]`（子串）、`ResidentApps[]`（精确名）、`SecurityApps[]`（名+签名方）、`ProtectedProcesses[]`（穷举名）。内容基线引用 [PRD 附录名单清单](../../PRD.md)。

### 1.5 事件契约（异步通知；同步结果走方法返回值）

- `TreeProgress(TreePlanId, TreeState)`：releaser → ui；树级粒度。
- `ReleaseCompleted(ReleaseReport)`：releaser → ui；**至多一次**；App 编排据此调 `IReleaseLogStore.Append`（单路径，无直调并行路径；ReleaseId 为幂等标识）。
- `ScanFailed(Error)`：scanner → ui；触发 [PRD §3.7"扫描失败"行](../../PRD.md)（列表保留旧结果，丢弃部分结果）。

## 2. 版本与兼容

契约变更流程（system 法-5）：先改本文件 → 评审 → 再改实现。v1 文件格式不设版本字段（单机自用、可重建）；破坏性语义变更以 spec 变更记录 + 显式迁移声明承载。事件/数据字段可新增（新增=兼容），删除或语义反转=破坏性（须评审记录）。

## 3. SLA / 非功能

分段预算（system §7 分解）：`TakeSnapshot` ≤2.0s、`CollectSignatures` ≤0.5s、`Classify` ≤0.3s（端到端 3s 含渲染，[PRD §3.4](../../PRD.md)）；`ReleaseCompleted` 端到端 ≤30s。
