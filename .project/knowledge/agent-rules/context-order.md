---
authority: authoritative
owner: 陈光洪
updated: 2026-09-14
applies_to: 全模块
---

# Agent 上下文读取顺序与规约

1. 读取顺序：项目 CLAUDE.md → 本账本（`.project/orchestrator-state.md`）→ [docs/项目阶段追踪.md](../../../docs/项目阶段追踪.md)（里程碑/验收记录）→ WBS（T-NN 任务定义）→ 对应 issue（AC 与排期窗口）。
2. 状态禁猜：任务状态只认 issue state + git log 实证，不认自由文本；账本是索引非副本。双源冲突消解：issue OPEN 但对应 T-NN 产物已合 main 且提交带 [reviewed] = 待 AC 采证关单，**不是可领缺口**；关单由日验收签字驱动。
3. 提交格式：中文提交消息 + 末尾 [reviewed]（cross-review 过后）；一任务一评审一提交。
4. 门禁命令：`pwsh scripts/gate.ps1` 全链通过方可交付。覆盖率口径（#29 裁决 2026-09-14）：机器门按全量 total 强制（阈值 80）作下限兜底；验收判读按全局门禁 5（分母 = 变更所及路径，>80% 达标）——三项目基线 ≥80%（以 gate.ps1 全链通过为准）时全量口径不会误拦达标变更（加权平均性质），变更自身覆盖 ≤80% 而全量漏放的由验收判读拦截；判读仍现差异时停下问 TL。
5. 不确定即停下问 TL，禁补全空白（本目录 authority=unconfirmed 的条目尤其如此）。
