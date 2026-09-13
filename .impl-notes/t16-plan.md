# T-16/T-27/#38 步骤 2 实施计划（工作稿）

## #38 步骤 2 裁决
释放报告数据源自 ReleaseReport（Items/双释放量/Before/After releaser 自采样），不消费 MainViewModel.Overview
→ 删除 MainViewModel 概览采样链（Overview/OverviewSummary/RefreshOverviewAsync/FormatOverview/InitializeAsync/
_overviewSampler 参数）；OverviewText 保留唯一格式化实现；清理对应测试断言与 MainWindow.Loaded 接线。

## T-27
- RestartOptions：`--memrelief-restart` + `--failed=<base64url(json[{"n","p"}])>`；32K 上限（32767）超限降级仅自动重扫
- SingleInstanceGuard：Mutex `Local\MemRelief.SingleInstance`；无参即夺即判；重启参有限等待（默认 10s）；激活既有窗口回调抽象
- App.OnStartup：解析→夺锁失败激活既有窗口退出；重启参自动扫+失败项传 VM

## T-16
- ReleaseAsync：勾选→Plan（RulePack 经 ScanCoordinator 单点）→确认弹窗（N=非空树数、X=Σ TreePrivateBytes）→
  ReleaseConfirmed→Task.Run(Execute)
- 收口单路径：ReleaseCompleted 事件（marshal 编组）→CompleteRelease：移除已结束项（Released/ForceKilled/Exited）
  +重建分组+报告文案（DisplayText）+IReleaseLogStore.Append 回填 LogPersisted
- 进度：TreeProgress 终态计数（Done/Skipped/Failed）→StatusText
- 关窗：PrepareClose（Releasing→Cancel+false）+SettleReleaseAsync（等 Execute+日志）
- 提权重启：矩阵加 RestartElevatedEnabled（ResultsShown/ReportShown）；内容条件在 VM；IAppRestarter（runAs）；
  UAC 拒绝 1223 停留普通权限；失败项高亮（名称 OrdinalIgnoreCase+路径均非空才比路径）不自动勾选

## 防御性边界（记录）
- Execute 意外异常（Core 结构上不可达：树内异常映射逐项+订阅者隔离）：文案提示+状态滞留 Releasing 为已知防御边界，
  不伪造报告
