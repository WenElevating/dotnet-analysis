# .NET 内存分析基础框架质量门证据

工作目录：`D:\AIProject\dotnet-analysis\.worktrees\foundation`
采集日期：2026-09-02
范围：Task 6 的 SDK、自动化验证、独立 QA 与最终仓库确认。

除 `git diff main -- src tests` 外，本记录保留命令的原始输出、时间戳和退出码。该 diff 已实际执行；因其完整输出为不可管理的大型源代码补丁，按审查要求不嵌入本记录，改以确定性的变更文件清单、提交摘要和已检查结论保留证据。

## SDK

命令：`dotnet --version`

```text
TIMESTAMP 2026-09-02T15:03:17.7498468+08:00
10.0.303
EXIT 0
```

## 自动化验证

命令：`dotnet restore DotnetAnalysis.sln`

```text
TIMESTAMP 2026-09-02T15:03:24.3943841+08:00
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
EXIT 0
```

命令：`dotnet build DotnetAnalysis.sln --configuration Debug --no-restore`

```text
TIMESTAMP 2026-09-02T15:03:31.7943253+08:00
  DotnetAnalysis.Core -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Core\bin\Debug\net10.0\DotnetAnalysis.Core.dll
  DotnetAnalysis.Application -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Application\bin\Debug\net10.0\DotnetAnalysis.Application.dll
  DotnetAnalysis.Diagnostics -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Diagnostics\bin\Debug\net10.0\DotnetAnalysis.Diagnostics.dll
  DotnetAnalysis.Desktop -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Desktop\bin\Debug\net10.0-windows\DotnetAnalysis.Desktop.dll
  DotnetAnalysis.Tests -> D:\AIProject\dotnet-analysis\.worktrees\foundation\tests\DotnetAnalysis.Tests\bin\Debug\net10.0-windows\DotnetAnalysis.Tests.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.95
EXIT 0
```

命令：`dotnet test DotnetAnalysis.sln --configuration Debug --no-restore`

```text
TIMESTAMP 2026-09-02T15:03:42.3044579+08:00
  DotnetAnalysis.Core -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Core\bin\Debug\net10.0\DotnetAnalysis.Core.dll
  DotnetAnalysis.Application -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Application\bin\Debug\net10.0\DotnetAnalysis.Application.dll
  DotnetAnalysis.Desktop -> D:\AIProject\dotnet-analysis\.worktrees\foundation\src\DotnetAnalysis.Desktop\bin\Debug\net10.0-windows\DotnetAnalysis.Desktop.dll
  DotnetAnalysis.Tests -> D:\AIProject\dotnet-analysis\.worktrees\foundation\tests\DotnetAnalysis.Tests\bin\Debug\net10.0-windows\DotnetAnalysis.Tests.dll
D:\AIProject\dotnet-analysis\.worktrees\foundation\tests\DotnetAnalysis.Tests\bin\Debug\net10.0-windows\DotnetAnalysis.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    41，已跳过:     0，总计:    41，持续时间: 174 ms - DotnetAnalysis.Tests.dll (net10.0)
EXIT 0
```

命令：`dotnet format DotnetAnalysis.sln --verify-no-changes --no-restore`

```text
TIMESTAMP 2026-09-02T15:03:56.3002043+08:00
EXIT 0
```

## 独立 QA

命令：`git diff main -- src tests`

```text
TIMESTAMP 2026-09-02T15:04:19.4585866+08:00
EXIT 0
```

完整 diff 已人工检查。它是相对空的 `main` 基线的基础框架新增，未显示 CLI 包装器、第三方 MVVM/事件总线、静态全局事件总线或 Desktop-to-Diagnostics 项目引用。下面是确定性的输出摘要。

命令：`git diff --name-only main -- src tests`

```text
TIMESTAMP 2026-09-02T15:04:19.5737545+08:00
COMMAND git diff --name-only main -- src tests
src/DotnetAnalysis.Application/Contracts/IAnalysisService.cs
src/DotnetAnalysis.Application/Contracts/ICaptureBackend.cs
src/DotnetAnalysis.Application/DotnetAnalysis.Application.csproj
src/DotnetAnalysis.Application/Events/AnalysisCanceled.cs
src/DotnetAnalysis.Application/Events/AnalysisCompleted.cs
src/DotnetAnalysis.Application/Events/AnalysisFailed.cs
src/DotnetAnalysis.Application/Events/CaptureProgressChanged.cs
src/DotnetAnalysis.Application/Events/CaptureStarted.cs
src/DotnetAnalysis.Application/Events/EventDeliveryException.cs
src/DotnetAnalysis.Application/Events/EventSubscriptionOptions.cs
src/DotnetAnalysis.Application/Events/IEventBus.cs
src/DotnetAnalysis.Application/Events/InProcessEventBus.cs
src/DotnetAnalysis.Application/Events/ModuleFaulted.cs
src/DotnetAnalysis.Application/Sessions/AnalysisSessionCoordinator.cs
src/DotnetAnalysis.Core/DotnetAnalysis.Core.csproj
src/DotnetAnalysis.Core/Events/IApplicationEvent.cs
src/DotnetAnalysis.Core/Sessions/AnalysisSession.cs
src/DotnetAnalysis.Core/Sessions/AnalysisSessionState.cs
src/DotnetAnalysis.Core/Sessions/AnalysisSessionTransitionRules.cs
src/DotnetAnalysis.Core/Sessions/SessionId.cs
src/DotnetAnalysis.Desktop/App.xaml
src/DotnetAnalysis.Desktop/App.xaml.cs
src/DotnetAnalysis.Desktop/AssemblyInfo.cs
src/DotnetAnalysis.Desktop/Composition/DesktopServiceCollectionExtensions.cs
src/DotnetAnalysis.Desktop/DotnetAnalysis.Desktop.csproj
src/DotnetAnalysis.Desktop/Infrastructure/AsyncRelayCommand.cs
src/DotnetAnalysis.Desktop/Infrastructure/IUiDispatcher.cs
src/DotnetAnalysis.Desktop/Infrastructure/ObservableObject.cs
src/DotnetAnalysis.Desktop/Infrastructure/RelayCommand.cs
src/DotnetAnalysis.Desktop/Infrastructure/WpfUiDispatcher.cs
src/DotnetAnalysis.Desktop/MainWindow.xaml
src/DotnetAnalysis.Desktop/MainWindow.xaml.cs
src/DotnetAnalysis.Desktop/ViewModels/ShellViewModel.cs
src/DotnetAnalysis.Diagnostics/DependencyInjection/DiagnosticsServiceCollectionExtensions.cs
src/DotnetAnalysis.Diagnostics/DotnetAnalysis.Diagnostics.csproj
tests/DotnetAnalysis.Tests/Application/AnalysisSessionCoordinatorTests.cs
tests/DotnetAnalysis.Tests/Application/InProcessEventBusTests.cs
tests/DotnetAnalysis.Tests/Core/AnalysisSessionTests.cs
tests/DotnetAnalysis.Tests/Desktop/AsyncRelayCommandTests.cs
tests/DotnetAnalysis.Tests/Desktop/CompositionTests.cs
tests/DotnetAnalysis.Tests/Desktop/ObservableObjectTests.cs
tests/DotnetAnalysis.Tests/Desktop/RelayCommandTests.cs
tests/DotnetAnalysis.Tests/DotnetAnalysis.Tests.csproj
tests/DotnetAnalysis.Tests/MSTestSettings.cs
EXIT 0
```

命令：`git diff --name-only main -- src tests | Measure-Object -Line`

```text
TIMESTAMP 2026-09-02T15:04:53.6785144+08:00

Lines Words Characters Property
----- ----- ---------- --------
   44
EXIT 0
```

命令：`git log --oneline main..HEAD`

```text
TIMESTAMP 2026-09-02T15:04:19.6579148+08:00
COMMAND git log --oneline main..HEAD
9500fe3 docs: clarify bounded event bus shutdown
0b3159a docs: record foundation quality gates
223304c fix: preserve command UI context
b0a71ce feat: add desktop application shell
3bbb373 fix: serialize coordinator cancellation boundaries
36096bf fix: serialize coordinator cancellation boundaries
c60ee49 feat: add analysis session coordinator
f60dce3 docs: clarify event bus progress tests
bcad8b4 fix: enforce bounded event delivery
8a83e85 feat: add typed in-process event bus
76d3cb2 feat: add analysis session state model
a66576b fix: enforce desktop x64 target
8b9a6ab chore: initialize dotnet analysis solution
ea594af docs: clarify foundation plan contracts
EXIT 0
```

命令：`rg -n -i "CommunityToolkit|Prism|ReactiveUI|MediatR|static.*EventBus|static.*IEventBus|dotnet-gcdump|dotnet-trace" src tests`

```text
TIMESTAMP 2026-09-02T15:04:27.3456360+08:00
tests\DotnetAnalysis.Tests\Application\AnalysisSessionCoordinatorTests.cs:340:    private static AnalysisSessionCoordinator CreateCoordinator(ICaptureBackend backend, IAnalysisService analyzer, IEventBus bus)
EXIT 0
```

唯一命中是测试私有静态工厂方法的 `IEventBus` 参数，不是静态字段、全局注册表或全局事件总线。

命令：`dotnet list DotnetAnalysis.sln package --include-transitive`

```text
TIMESTAMP 2026-09-02T15:04:27.4652177+08:00
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
项目“DotnetAnalysis.Core”具有以下包引用
   [net10.0]: 未找到此框架的任何包。
项目“DotnetAnalysis.Application”具有以下包引用
   [net10.0]:
   顶级包                                              已请求       已解决
   > Microsoft.Extensions.Logging.Abstractions      10.0.11   10.0.11

   可传递的包                                                        已解决
   > Microsoft.Extensions.DependencyInjection.Abstractions      10.0.11

项目“DotnetAnalysis.Diagnostics”具有以下包引用
   [net10.0]:
   顶级包                                                          已请求       已解决
   > Microsoft.Extensions.DependencyInjection.Abstractions      10.0.11   10.0.11

   可传递的包                                            已解决
   > Microsoft.Extensions.Logging.Abstractions      10.0.11

项目“DotnetAnalysis.Desktop”具有以下包引用
   [net10.0-windows]:
   顶级包                                             已请求       已解决
   > Microsoft.Extensions.DependencyInjection      10.0.11   10.0.11
   > Microsoft.Extensions.Logging                  10.0.11   10.0.11

   可传递的包                                                        已解决
   > Microsoft.Extensions.DependencyInjection.Abstractions      10.0.11
   > Microsoft.Extensions.Logging.Abstractions                  10.0.11
   > Microsoft.Extensions.Options                               10.0.11
   > Microsoft.Extensions.Primitives                            10.0.11

项目“DotnetAnalysis.Tests”具有以下包引用
   [net10.0-windows]:
   顶级包                                              已请求       已解决
   > Microsoft.Extensions.DependencyInjection       10.0.11   10.0.11
   > Microsoft.Extensions.Logging.Abstractions      10.0.11   10.0.11
   > MSTest                                         4.0.2

   可传递的包                                                        已解决
   > Microsoft.ApplicationInsights                              2.23.0
   > Microsoft.CodeCoverage                                     18.0.1
   > Microsoft.DiaSymReader                                     2.0.0
   > Microsoft.Extensions.DependencyInjection.Abstractions      10.0.11
   > Microsoft.Extensions.DependencyModel                       6.0.2
   > Microsoft.Extensions.Logging                               10.0.11
   > Microsoft.Extensions.Options                               10.0.11
   > Microsoft.Extensions.Primitives                            10.0.11
   > Microsoft.NET.Test.Sdk                                     18.0.1
   > Microsoft.Testing.Extensions.CodeCoverage                  18.1.0
   > Microsoft.Testing.Extensions.Telemetry                     2.0.2
   > Microsoft.Testing.Extensions.TrxReport                     2.0.2
   > Microsoft.Testing.Extensions.TrxReport.Abstractions        2.0.2
   > Microsoft.Testing.Extensions.VSTestBridge                  2.0.2
   > Microsoft.Testing.Platform                                 2.0.2
   > Microsoft.Testing.Platform.MSBuild                         2.0.2
   > Microsoft.TestPlatform.AdapterUtilities                    18.0.1
   > Microsoft.TestPlatform.ObjectModel                         18.0.1
   > Microsoft.TestPlatform.TestHost                            18.0.1
   > MSTest.Analyzers                                           4.0.2
   > MSTest.TestAdapter                                         4.0.2
   > MSTest.TestFramework                                       4.0.2
   > Newtonsoft.Json                                            13.0.3

EXIT 0
```

## 最终仓库确认

以下输出在证据首次提交 `ea2ade4` 后采集；三项 Task 6 最终仓库确认命令均成功。

命令：`git status --short`

```text
TIMESTAMP 2026-09-02T15:08:35.5382175+08:00
COMMAND git status --short
EXIT 0
```

命令：`git diff --check`

```text
TIMESTAMP 2026-09-02T15:08:35.6693849+08:00
COMMAND git diff --check
EXIT 0
```

命令：`git log --oneline -6`

```text
TIMESTAMP 2026-09-02T15:08:35.7845059+08:00
COMMAND git log --oneline -6
ea2ade4 docs: preserve foundation quality gate evidence
9500fe3 docs: clarify bounded event bus shutdown
0b3159a docs: record foundation quality gates
223304c fix: preserve command UI context
b0a71ce feat: add desktop application shell
3bbb373 fix: handle synchronous lifecycle publication failures
EXIT 0
```

补充提交质量检查 `git show --check --stat --oneline HEAD` 曾报告本文件第 3、4 行尾随空白（退出码 2）。该发现仅涉及 Markdown 格式，已在本提交中删除；后续提交后将再次运行 `git diff --check` 与 `git show --check`。
