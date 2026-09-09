# MemRelief.TestProcs（T-20 孤儿测试进程构造器）

真机验收复用工具（R01/R03）：构造 PRD §3.2 前置构造规格形态的孤儿进程与降级场景进程。独立 WinExe 项目，不进单文件发布（data-contracts §2 grilling 裁决⑩）。WinExe 无控制台：错误详情不可见，退出码 0=成功 / 1=参数错误 / 2=就绪超时，用法以本文件为准。

## 用法

```
MemRelief.TestProcs parent [--mem-mb N] [--cpu] [--established] [--out-pid <file>]
MemRelief.TestProcs child  [--mem-mb N] [--cpu] [--established]
```

- **parent**：启动 child → 等 child 就绪（命名事件，10s 超时）→ 本进程退出 → child 成为孤儿（父 PID 指向已退出进程）。`--out-pid` 文件首行=child pid。
- **child**：挂起等待被外部终止。由**活父**（脚本/测试进程）直接启动即为"同目录存活进程"形态；pid 捕获示例（PowerShell）：`(Start-Process <exe> -ArgumentList 'child','--mem-mb','10' -PassThru).Id`。

## 场景矩阵（与降级信号映射；「终局判定」= rules 消费全部信号后的分级）

| 场景 | 启动方式 | 终局判定影响 |
|---|---|---|
| 纯净孤儿（基线形态） | `parent` | ✅：OrphanHint=ParentDead，无窗口/无连接/非服务 |
| CPU 忙（孤儿） | `parent --cpu`（child 内双自旋线程） | ⚠️：口径 #7 差分 >1s |
| 活跃连接（孤儿） | `parent --established`（自连回环：一条逻辑连接，TCP 表两端各一行、同 pid 计 2） | ⚠️：口径 #6（计数 >0 即活跃） |
| 同目录存活（非孤儿旁证） | 活父先启动 `child --mem-mb 10`（PassThru 记 pid），再跑 `parent` | 基线孤儿 ✅ 降 ⚠️：口径 #3 旁证（同目录存活者为非孤儿） |
| 非孤儿小体量 | 活父直启 `child --mem-mb 10` | ⚠️：口径 #13 <50MB 占用过小 |
| 孤儿小体量（豁免验证） | `parent --mem-mb 10` | ✅：孤儿豁免小体量降级（验证豁免路径本身） |
| 常驻名命中 / UWP | 不由本工具构造（避免复制自身为常驻名的侵入，AC 收窄已登记 issue #10）——按 WBS 用真机实存项（开微信/任一 UWP 应用） | ⚠️：口径 #5 / UWP 特例 |

## 停止

全部构造进程挂起等待外部终止：按 pid `taskkill /F /PID <pid>`；pid 文件丢失或泄漏残留时按映像名兜底全量回收 `taskkill /F /IM MemRelief.TestProcs.exe`。真机端到端复用的自动化样例见 `tests/MemRelief.Core.Tests/Scanner/TestProcsIntegrationTests.cs`。
