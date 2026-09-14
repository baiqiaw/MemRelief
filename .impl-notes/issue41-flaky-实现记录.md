# #41 flaky 修复实现记录（尸体歧义消解）

日期：2026-09-14 ｜ issue：#41 ｜ 关联：#40（同域，未动）

## 根因（探针实测，非推演）

临时探针（收尾已删，脚本要点：spawn TestProcs child → TerminateProcess → 句柄全释放后紧循环 OpenProcess 观察）实测：

1. 进程 terminated（signaled ~13ms）后，**尸体对象仍可 OpenProcess 约 26–150ms**（探针首版自持 `Process` 句柄撑活对象，修正句柄卫生后测得真实窗口）。
2. 窗口内：`GetProcessTimes` 正常且创建时间与快照**精确一致**；`QueryFullProcessImageName` **失败 err 31（ERROR_GEN_FAILURE）**——终止态对象的 Win32 固有行为。
3. → `Win32LiveProcess.IdentityMatches`：创建时间 OK 但名称不可读 → fail-closed false → `ProcessReleaser` 归 `IdentityChanged`。
4. 测试时序（AssertGone ~50ms 探到 signaled → Execute TryOpen ~+20–60ms）恰落窗口内 → 期望 Exited 实得 IdentityChanged。当日 2/2 失败 / 次日 3/3 通过 = 竞态随机器状态漂移。

排除 M1（PID 复用）：复用对象必然存活（WaitForSingleObject 超时），与本例 signaled 状态不符；且毫秒级窗口内 PID 复用需计数器回绕，不成立。

## 修法（issue 方向②，TL=接单人裁决）

产品判定链消解尸体歧义，而非测试侧时序锚（方向①治标：窗口内误分类仍在，只是测试绕开）：

- `ProcessReleaser` 身份不一致分支：`CreationTimeMatches && WaitExit(0)`（创建时间一致 + 对象已终止）→ `Exited`；其余保持 `IdentityChanged` fail-closed 不变。
- 安全性：两分支都不执行任何结束动作（exactly 同跳过语义），只改分类标签；防复用杀错（PRD F3-2）不削弱——安全单锚=创建时间精确必异（复用须旧进程先终结，复用者创建时间严格晚于快照值，FILETIME 100ns 精度下精确相等物理不可达）。
- 附带修复产品面真实误报：扫描→执行间自然退出的进程此前会被报"身份变更"（可疑事件），现在如实报"已退出"。
- `ILiveProcess` 新增 `CreationTimeMatches`（接口+Win32+fake 三侧）；`Win32LiveProcess.IdentityMatches` 重构为复用该方法（创建时间判定单一事实源，语义不变）。

## TDD

- 红灯：`Execute_终止态对象创建时间一致_名称不可读归Exited`（1 失败，精确命中行为变更点）
- 绿灯后守卫：`Execute_终止态对象创建时间不一致_仍IdentityChanged`（异进程尸体保守不变）、`Execute_存活对象创建时间一致但名称不可读_仍IdentityChanged`（终止态前提不满足不放行）
- 哨兵快照（MinValue）：`CreationTimeMatches` false → IdentityChanged 不变，由既有 `身份不可判_同IdentityChanged兜底` 同语义覆盖

## 验证

- 合成 28/28、Core 全量 429/429、集成类 8/8、flaky 用例单跑 5/5、`gate.ps1` 全链通过（含覆盖率门）
- 证据链：探针实测旧机制（ctMatch=True + QFPI err31）→ 合成红灯转绿 → ctMatch=True 同时实证 `CreationTimeMatches` 的 GetProcessTimes 读取路径在真实尸体上成立

## 残留观察（不阻塞）

- 真机集成测试无法确定性构造"落在尸体窗口内"（抢 ~100ms 窗口本身会引入新 flaky），窗口内路径由合成测试承载；探针一次性实测兜底。
- Bench 覆盖率 80.73% 贴线（本次改动不触及 Bench，属存量现状）。
