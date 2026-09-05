# storage 模块规格

## 0. 追溯

覆盖需求：R04（白名单）、R06（释放日志）；支撑 R01（四份内置名单资源）。上游：[PRD](../../PRD.md)。

## 1. 职责

**管**：白名单文件 CRUD（含元数据）与快照读、释放日志追加与轮转（5MB×3）、损坏自愈（.corrupt 备份/行级跳过）、**四份内置名单**资源加载（残留模式库/常驻应用/安全软件/系统保护名单）、用户数据目录锚定。
**不管**：判定中如何使用名单（rules）、日志内容语义（releaser 产出）。

## 2. 领域概念

- **用户数据目录锚定**：白名单/日志固定落当前用户数据目录；以管理员重启后仍同一目录（[PRD §3.4 安全段](../../PRD.md)）。
- **快照读**：白名单以不可变 `WhitelistSnapshot` 供给（一次扫描一个一致视图，由编排方装载注入 rules）。

## 3. 数据模型

见 [data-contracts.md](../interfaces/data-contracts.md)：`WhitelistEntry`、`WhitelistSnapshot`、`ReleaseLogEntry`（落盘 schema = [PRD F6 JSONL](../../PRD.md)；内存契约→落盘的时区/字段映射归本模块落盘层）、`RulePack`（四数组）。

## 4. 行为

### 4.1 关键用例
- **白名单**：`Add/Remove/List/Snapshot`；损坏时改名保留 `.corrupt` + 重建空白（[PRD §3.7](../../PRD.md)）。
- **日志**：`Append(ReleaseReport)`（App 编排在 ReleaseCompleted 后调用，单路径）；达到 5MB 滚动，保留最近 3 副本；写失败**不阻塞释放**，返回失败结果由 ui 呈现"本次结果未留痕"（[PRD §3.7](../../PRD.md)）；行级自愈（跳过损坏行）。
- **名单加载**：`LoadRulePack()` → 读四份名单资源；**保护类名单（安全软件/系统保护名单）加载失败 → 上报失败并由编排方使 rules 按 system 法-3 保守兜底（相关进程不进✅级），不得按空名单放行**（fail-safe）。

### 4.2 状态机

无资源状态机（文件 CRUD + 追加）。

### 4.3 时序图

跨模块时序见 [rules §4.3](./rules.md) 与 [releaser §4.3](./releaser.md) 中 storage 数据的装载/追加位（均经编排方，模块被动响应）。

## 5. 接口依赖

- 提供：`IWhitelistStore`（含 `Snapshot()`）、`IReleaseLogStore`（含写结果返回）、`IRulePackStore`（含加载失败上报）。消费者：App（编排装载）、ui（白名单管理面板）。
- 消费：releaser 产出的 `ReleaseReport`（经 App 编排传入）。

## 6. 约束（模块级）

- **法**：自有数据文件仅白名单与日志两个（+轮转副本）；除此之外零文件写入（system 法-4 模块落地）。
- **法**：保护类名单加载失败走保守兜底（§4.1），禁止空名单放行。
- **法**：白名单文件 schema 变更走 [data-contracts.md](../interfaces/data-contracts.md) 变更流程。
- **情报**：文件读写建议原子写（临时文件 + replace）防中途崩溃损坏。
