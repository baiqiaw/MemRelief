---
authority: authoritative
owner: 陈光洪
updated: 2026-09-07
applies_to: 全模块
---

# 系统结构与运行入口

- 模块边界与接口契约：[docs/specs/system-spec.md](../../../docs/specs/system-spec.md) + modules/（scanner/releaser/rules/storage/ui）+ [interfaces/data-contracts.md](../../../docs/specs/interfaces/data-contracts.md)。
- 代码结构：`src/MemRelief.Core`（判定/采集/存储，net10.0，无 UI 依赖）+ `src/MemRelief.App`（WPF 壳，net10.0-windows）+ `tests/MemRelief.Core.Tests`（xunit，`dotnet test` 载体与覆盖率门对象）；App→Core 单向引用，Core 编译产物禁引 WPF/UI 程序集（`scripts/check-core-refs.ps1` 产物级强制）。
- 运行入口：`dotnet build MemRelief.sln`；`dotnet test`；质量门禁 `pwsh scripts/gate.ps1`（全链门禁，覆盖率阈值由其注入）。
- 工期、车道、关键路径与日历口径：以 [docs/Schedule.md](../../../docs/Schedule.md) 为事实源。
