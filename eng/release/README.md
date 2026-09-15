# 发布 V1

生产包固定为 Windows x64 自包含目录包，不依赖用户预装 .NET SDK。

## 构建与校验

在仓库根目录运行：

```powershell
.\eng\Publish-Release.ps1 -Version 1.0.0
```

脚本会执行 Release x64 构建、`win-x64` 自包含发布、生成 `release-manifest.json`（包含提交号和 SHA-256），并将校验证据写入 `TestResults\ReleaseAcceptance-*`。

已有发布目录可单独校验：

```powershell
.\eng\Verify-ReleasePackage.ps1 -PackagePath .\artifacts\release\DotnetAnalysis-1.0.0-win-x64
```

发布前仍必须执行 Windows 诊断集成、三运行时目标、压力和干净机器验收；包校验通过不等于功能验收通过。

## 完整验收

为避免单元测试项目与 Windows 诊断测试项目并行争用目标进程、EventPipe 和磁盘资源，使用串行验收入口：

```powershell
.\eng\Run-ReleaseAcceptance.ps1 -Version 1.0.0 -RunLargeSnapshotBenchmark
```

该入口按顺序执行单元测试、Windows 诊断集成测试、可选大快照基准，最后生成并校验自包含发布包。不要用 Solution 级 `dotnet test` 作为唯一发布门禁。

## 夜间极限验收

`Run-NightlyDiagnosticsStress.ps1` 会串行执行显式长测，并将控制台日志和结果索引写入 `TestResults\NightlyDiagnosticsStress-*`。它默认不执行任何长测，必须显式指定项目：

```powershell
.\eng\Run-NightlyDiagnosticsStress.ps1 -RunExtremeHeap -RunResourceCycles -RunRetentionBenchmark -RunExecutionBenchmark -RunExecutionSoak
```

默认极限堆对象数为 `5,000,000` 和 `10,000,000`；可先用较低档验证环境：

```powershell
.\eng\Run-NightlyDiagnosticsStress.ps1 -HeapObjectCounts 100000,1000000 -RunExtremeHeap -RunResourceCycles
```

建议晚上使用第一条命令，并保持电源、磁盘和目标机器稳定。脚本按顺序执行，避免多个测试宿主同时争用 EventPipe、Profiler、CPU 和临时磁盘。中断后应保留对应证据目录，检查 `summary.json` 和 `console.log`。

也可以直接双击仓库根目录的 `Run-NightlyDiagnosticsStress.bat`，或在 CMD 中运行：

```cmd
Run-NightlyDiagnosticsStress.bat
```

BAT 会自动检查 `dotnet` 是否在 PATH 中，并在结束时保留退出码和暂停窗口；脚本中断或失败时，证据仍保留在 `TestResults\NightlyDiagnosticsStress-*`。
