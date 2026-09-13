# T-19 实现记录（issue #21：单文件发布配置与自用说明）

## publish 配置落点

`src/MemRelief.App/Properties/PublishProfiles/win-x64.pubxml`（走发布 profile，不写 csproj——普通 `dotnet build` 不携带 RID/单文件属性，日常构建行为零变化）。

关键属性与理由：

| 属性 | 理由 |
|------|------|
| `SelfContained=true` + `RuntimeIdentifier=win-x64` | 免安装（目标机无需 .NET 运行时），PRD §3.4 技术约束 |
| `PublishSingleFile=true` | 单文件 AC |
| `IncludeNativeLibrariesForSelfExtract=true` | WPF 原生依赖（wpfgfx/PenImc/D3DCompiler/vcruntime）不进包则散落 exe 旁，单文件不成立 |
| `SatelliteResourceLanguages=zh-Hans` | 不设则运行时语言包目录（cs/ de/ …）残留 publish 目录，破坏单文件 |
| `DebugType=None` + `DebugSymbols=false` | 关 App 自身 pdb |
| `AllowedReferenceRelatedFileExtensions=none` | 引用工程（Core）pdb 拷贝过滤；**置空字符串无效**（SDK 视为未设回落默认清单），须设伪扩展名 |

取舍记录：未启用 `EnableCompressionInSingleFile`（约可减 30–40% 体积，代价是每次启动解压变慢），未启用 ReadyToRun（启动提速换体积增大）——自用无体积/启动 AC，保持默认。self-contained 将 .NET 运行时冻结进 exe，运行时安全修复须经重新发布带入。

发布命令：`dotnet publish src/MemRelief.App -c Release -p:PublishProfile=win-x64`

## 产物验证证据

- publish 目录：`src/MemRelief.App/bin/Release/net10.0-windows/win-x64/publish/`，清单仅 `MemRelief.App.exe`（131,942,807 字节 ≈ 126 MB，self-contained WPF 合理体积；未开 ReadyToRun/裁剪——WPF 不支持 trim，自用取体积外保守）。
- 冒烟（worktree 内实跑）：
  1. 启动前 `tasklist //FI "IMAGENAME eq MemRelief.App.exe"` → 无匹配（无残留，也排除误杀用户实例）；
  2. `./MemRelief.App.exe &`，6 秒后 tasklist → `MemRelief.App.exe PID 22092`（进程出现 = 免安装可直接启动）；
  3. `taskkill //F //IM MemRelief.App.exe` → 成功终止 PID 22092；复查 tasklist → 无匹配（零残留）。
- 注：产物名是 `MemRelief.App.exe`（程序集名），非 MemRelief.exe。

## README 取材来源映射（README.md，仓库根）

| README 章节 | 来源 |
|------|------|
| 快速开始/系统要求 | PRD §3.4 兼容性（v1 实测范围 Windows 11 x64）、不常驻不轮询口径 |
| 三级列表/右键加白 | WBS T-15 行（✅默认勾/⚠️默认不勾/🚫折叠不可勾；右键加白仅✅⚠️） |
| 需管理员权限场景 | PRD §3.4 权限方案对比与 §3.7 F3（普通权限覆盖主场景；跨用户/服务/PPL 预标"需管理员"——RulesEngine 预标实现实证）；操作路径写"右键以管理员身份运行"而非应用内提权重启入口——入口归 open 的 T-27（runas 未实现），README 不承诺未交付 UI |
| 数据目录位置/文件表 | StorageShared.DefaultDataDirectory（LocalApplicationData+ProductInfo.Name）+ WhitelistStore（whitelist.json）+ ReleaseLogStore（releases.jsonl，5MB×3 轮转）+ storage.md §4.1/§6（.corrupt 自愈、零其他文件写入法-4） |
| 已知边界 1-3 | issue #32 边界登记第 1/2/3/4 条的白话转写（一次性触发器计周期+禁用任务仍展示、双视图重复 SourceEntry 无展示去重实现——已 grep src 实证无 Distinct 于来源链、HKLM32 %var% 不展开） |
| 已知边界 4-5 | issue #35 第 1/2 条的白话转写（验签失效候选全部降🚫不推荐组——行为实现见 RulesEngine Unverifiable 分支、WinVerifyTrust 无超时 cryptsvc 停滞等待、网络共享路径 mtime 无时限护栏） |
| 便携换机取舍 | PRD §3.4 技术约束（"便携换机时白名单/日志不随行，为已接受取舍"字面）+ StorageShared 按用户锚定 |
| 卸载与数据清除段 | 用户运维视角的程序/数据分离（删 exe 即卸载、关程序后删数据目录即清数据）；AC 第 3 条的仓库视角回滚 = 本变更 3 个纯新增文件，`git revert <合入提交>` 即完整回退，无数据迁移、无 schema 变更 |

## 门禁基线执行说明（与 gate.ps1 的等价性）

PowerShell 系调用被 worktree 隔离 hook 拦截（无法静态证明脚本内 git 目标），四段门禁等价直跑：

1. `dotnet build MemRelief.sln -c Debug` → 0 错误（105 个既有警告，与基线一致）；
2. check-core-refs.ps1 等价：`grep -aic -E "WindowsBase|Presentation|System\.Windows\.|UIAutomation|System\.Xaml|ReachFramework|System\.Printing" MemRelief.Core.dll` → **0 命中**（程序集引用名必落在文件字节，零命中为 PASS 充分条件，强于脚本白名单比对）；文件内 `MemRelief.Core` 身份串 6 处（身份佐证）；
3. Core 测试+覆盖率：97.54% line / 91.1% branch（阈值 80）→ 通过；
4. App 测试+覆盖率：97.09% line / 90.64% branch（阈值 80）→ 通过。

TL 合并后建议在主仓跑一次 `pwsh scripts/gate.ps1` 原生口径复核。

## 遗留/边界

- issue #21 前置字段标"T-14 未就绪"已滞后：T-14 于 6704f06 签字关单（git 实证），本单按派发指令执行。
- publish 配置与 README 均未触碰 src/ C# 交互逻辑与 docs/specs（车道边界遵守）；pubxml 属 csproj 发布域配置文件，非交互逻辑。
- 应用内"以管理员身份重启"入口未实现（T-27 范围），README 已按现状措辞；T-27 交付后 README 使用要点段可同步更新。

## cross-review 修复记录（7 维度混合全量评审）

- 评审轮次：7 子代理并行全量 → 有效发现 22 项（高置信 ≥70 过 14 项内容验证全部 VALID；低置信但有据 8 项并入修复）。
- README 主要修复：①%TEMP% 自解压缓存披露（5 维度命中，实证 `%TEMP%\.net\MemRelief.App\` 存在）——"不写任何其他文件"收窄为数据文件口径、卸载段补临时目录清理；②卸载段补"先关闭程序"前置（运行中删数据目录后内存白名单会经下次加白整体重写复活，维度 7 构造复现）；③边界④改写为实际行为（候选降入🚫不推荐组、推荐组可能为空——原稿"标为无法验证/列表整体为空"与 RulesEngine Unverifiable 分支及 UI 实况不符）；④组名/右键菜单对齐 UI 实际文案（DisplayText.cs"推荐可释放/谨慎/不推荐"、MainWindow.xaml"此进程永不推荐"）；⑤.corrupt 自愈主语限定白名单（日志走行级跳过，ReleaseLogStore 无 .corrupt 机制）；⑥边界⑤补 #35 第 2 条后半（网络共享路径等待）；⑦构建段补 SDK 前置/脏 publish 目录清理/关闭运行中应用（增量发布不清理旧产物——本任务实测踩中 pdb 残留）。
- pubxml 修复：删 Configuration/Platform/DebugSymbols 三行惰性实体（MSBuild 全局属性不可被 pubxml 覆盖、Platform 不参与 SDK 输出路径、DebugType=None 已单独充分）；SatelliteResourceLanguages 注释理据改正（net5+ 单文件本就打包 satellite，该属性收益是减体积非防目录残留）。
- pubxml 变更后复测：重新 publish 通过，发布目录仍仅 MemRelief.App.exe 单文件（详见交付报告）。
