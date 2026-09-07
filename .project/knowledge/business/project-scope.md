---
authority: authoritative
owner: 陈光洪
updated: 2026-09-07
applies_to: 全模块
---

# 业务范围与验收口径

- 项目目标、边界、GWT 验收：见 [docs/PRD.md](../../../docs/PRD.md)（v1.3 为范围基线）。
- 目标用户与场景：PRD §1.4（v1 为单一用户自用，无甲方角色）；无集中术语表，术语口径见 PRD §3.1 F1 口径表与 [docs/specs/interfaces/data-contracts.md](../../../docs/specs/interfaces/data-contracts.md)。
- 内存释放判定口径唯一事实源 = PRD §3.1 F1 处理逻辑与口径表 #1–#15（system-spec 法-2）；[docs/specs/modules/rules.md](../../../docs/specs/modules/rules.md) 为模块契约（职责边界与行为），判定语义全部引用自 PRD F1，不在此复述。
- 不可破坏规则：白名单进程不释放；树保护（父子进程约束）见 [docs/specs/modules/releaser.md](../../../docs/specs/modules/releaser.md)。
