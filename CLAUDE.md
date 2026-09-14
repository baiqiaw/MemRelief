# MemRelief（内存管理工具）

本仓启用 issue 任务口径（见全局规范附录 A）。

issue 落点（覆盖全局附录 H 默认）：本仓为 GitHub 仓库（baiqiaw/MemRelief），issue 用 `gh` cli 创建与操作，不建本地台账。

会话入口：项目编排账本 `.project/orchestrator-state.md` + 知识底座 `.project/knowledge/`（读取顺序与状态规约见 `.project/knowledge/agent-rules/context-order.md`）。

覆盖率口径（#29 裁决 2026-09-14）：机器门 `scripts/gate.ps1` 按全量 total ≥80%（Core/App/Bench）作下限兜底；验收判读仍按全局规范门禁 5（分母 = 变更所及路径，>80% 达标）。三项目基线 ≥80%（以 gate.ps1 全链通过为准）时全量口径不会误拦达标变更（加权平均性质），变更自身覆盖 ≤80% 而全量漏放的由验收判读拦截。
