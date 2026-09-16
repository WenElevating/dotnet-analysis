param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [switch]$SkipBuild,
    [switch]$RunLargeSnapshotBenchmark
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        & dotnet build .\DotnetAnalysis.sln --configuration $Configuration --property:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    }

    & dotnet test .\tests\DotnetAnalysis.Tests\DotnetAnalysis.Tests.csproj --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }

    & dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj `
        --configuration $Configuration --no-build --filter 'TestCategory=WindowsDiagnosticsIntegration'
    if ($LASTEXITCODE -ne 0) { throw 'Windows diagnostics integration tests failed.' }

    if ($RunLargeSnapshotBenchmark) {
        $env:DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK = 'true'
        & dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj `
            --configuration $Configuration --no-build `
            --filter 'FullyQualifiedName~LargeSnapshotBenchmark_RecordsCaptureAndCachedQueryMeasurements'
        if ($LASTEXITCODE -ne 0) { throw 'Large snapshot benchmark failed.' }
    }

    $orchestrationGateVariables = @(
        'DOTNET_ANALYSIS_RUN_ORCHESTRATION_PERFORMANCE',
        'DOTNET_ANALYSIS_RUN_ORCHESTRATION_STRESS'
    )
    $previousOrchestrationGateValues = @{}
    foreach ($variableName in $orchestrationGateVariables) {
        $previousOrchestrationGateValues[$variableName] = [Environment]::GetEnvironmentVariable($variableName, 'Process')
        Remove-Item "Env:$variableName" -ErrorAction SilentlyContinue
    }

    try {
        & (Join-Path $PSScriptRoot 'Run-OrchestrationAcceptance.ps1') -Configuration $Configuration -SkipBuild
        if ($LASTEXITCODE -ne 0) { throw 'Orchestration acceptance failed.' }
    }
    finally {
        foreach ($variableName in $orchestrationGateVariables) {
            $previousValue = $previousOrchestrationGateValues[$variableName]
            if ($null -eq $previousValue) {
                Remove-Item "Env:$variableName" -ErrorAction SilentlyContinue
            }
            else {
                Set-Item "Env:$variableName" $previousValue
            }
        }
    }

    & (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Version $Version -SkipBuild
    if ($LASTEXITCODE -ne 0) { throw 'Release package verification failed.' }
    Write-Output 'Release acceptance passed using sequential test project execution.'
}
finally {
    Remove-Item Env:DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK -ErrorAction SilentlyContinue
    Pop-Location
}
