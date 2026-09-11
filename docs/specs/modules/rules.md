# rules 模块规格

## 0. 追溯

覆盖需求：R01（三级判定）、R02（判定依据数据）。上游：[PRD](../../PRD.md)。

## 1. 职责

**管**：对 `ScanResult` 应用三级规则（✅/⚠️/🚫）与冲突消解（🚫 > ⚠️ > ✅）、旁证降级、**名单匹配派生判定（残留模式库/常驻应用/安全软件/白名单/保护名单——四份名单+白名单的命中计算）**、树合计内存、"需管理员"预标（口径 #8 服务判定 + 跨用户/受拒进程）、候选预筛、搜索查询判定。
**不管**：信号采集与验签（scanner）、结束执行（releaser）、文案呈现（ui）。

## 2. 领域概念

- **判定依据（Basis）**：一条命中规则的机器可读记录（`SignalId`=口径表编号 + Detail），原因文案由 ui 映射（system §6 情报-1）。
- **冲突消解**：多级命中取最保守级（[PRD F1-5](../../PRD.md)）。
- **预标（RequiresElevation）**：口径 #8 判为服务、或所有者非当前用户/句柄受拒的进程，预标"需管理员"（[PRD F3"扫描阶段即预标"]）。

## 3. 数据模型

见 [data-contracts.md](../interfaces/data-contracts.md)：`Classification`（含 `RequiresElevation`）、`QueryResult`。关系：每 `ProcessSnapshot` 1..1 `Classification`。

## 4. 行为

### 4.1 关键用例
- **分类**：`Task<IReadOnlyList<Classification>> Classify(ScanResult, WhitelistSnapshot, RulePack, ClassificationContext)` → 逐进程：采集型信号求值 + 名单匹配派生（四份名单+白名单）→ 冲突消解 → 预标 → 输出。判定语义全部引用 [PRD F1 处理逻辑 2–5 与口径表](../../PRD.md)，不在此复述。`ClassificationContext.SelfPid` 供"本工具自身"🚫判定；纯函数约束下环境信息一律经此参数注入（2026-09-05 契约修订，见 data-contracts §1.1）。**交付切分（T-06/T-07）**：Classify 主链含服务[口径#8]与受拒[口径#11]预标（T-06 交付）与跨用户预标（T-07 交付，[data-contracts §2](../interfaces/data-contracts.md) 裁决记录）。输出含全部进程（Unmatched/Whitelisted 项也在内，1..1 契约），「不进列表」的过滤由编排方/ui 执行；全量集供 Query 直接复用。
- **候选预筛**：`ISet<int> CandidateIds(ScanResult)` → 需验签的候选（潜在✅/⚠️且非系统目录），供编排方驱动 `CollectSignatures`（[scanner §4.3](./scanner.md)）。谓词保守过包含：排除且仅排除「无论签名结果如何终局必🚫」者（该 Pid 有 AccessDenied 失败；非 UWP 且 `IsSystemDirectory != false`——null 与 Classify 同向保守视为系统目录；非 UWP 且 `HasVisibleWindow != false`——null 保守视为有窗口；服务且失败恢复配置为不恢复）。UWP 包进程终态在本谓词可见信号范围内仅受签名影响（⚠️/🚫），除 AccessDenied/服务不恢复外一律入选。白名单/本工具自身/名单命中进程不在单参数入参可判范围，一律入选（多验无害，Classify 终局闸门兜底，v1 已知边界）。
- **查询**：`IReadOnlyList<QueryResult> Query(ScanResult scan, IReadOnlyList<Classification> classifications, string? name, int? pid)` → 消费编排方持有的 Classify 全量输出定位目标（不重算判定）；同名多进程全部返回，Pid 优先于名（名参数忽略），两者均空 → 空集，classifications 缺项的进程不返回；Outcome 含"未命中规则"（Unmatched）/"白名单排除"（Whitelisted）（[PRD F2](../../PRD.md)；QueryResult 定形见 [data-contracts §1.2](../interfaces/data-contracts.md)）。

### 4.2 状态机

无（纯函数模块）。

### 4.3 时序图

见 [scanner §4.3](./scanner.md)（rules 为编排链中的被调方；白名单快照与 RulePack 由编排方从 storage 装载后参数注入——本模块**不发起 I/O**）。

## 5. 接口依赖

- 提供：`IRulesEngine`（`Classify`、`CandidateIds`、`Query`）。消费者：App（编排）。
- 消费：scanner 的 `ScanResult`（参数注入）；storage 的 `WhitelistSnapshot` 与 `RulePack`（**经编排方参数注入，模块不直接依赖 storage**）。

## 6. 约束（模块级）

- **法**：**判定求值为纯函数**——同输入（ScanResult+WhitelistSnapshot+RulePack+ClassificationContext）必得同输出；不发起任何 I/O、不持有可变状态（M1 以 xUnit 驱动验收的直接依据）。
- **法**：每条 `Classification` 的依据可完整追溯（口径表编号命中/未命中）——GWT R01 系列断言基础。
- **法**：白名单进程完全排除；保护名单不可勾选；`Classification.TreePrivateBytes` = 口径 #13 树合计（该进程全部后代私有提交，2026-09-08 T-08 裁决③收口：本模块计算，供排序/R02"树合计占用"展示/小体量降级判定；确认弹窗"将结束约 X MB"与释放量预估数据源为 `TreePlan.TreePrivateBytes`（将结束节点合计，见 [data-contracts §2 T-08 裁决③](../interfaces/data-contracts.md)），非本字段）。
- **法**：名单匹配输入缺失（RulePack 加载失败经编排方传入空）时，按 system 法-3 保守兜底——相关保护性依据缺失的进程不进✅级。
