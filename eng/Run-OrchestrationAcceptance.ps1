[CmdletBinding()]
param([string]$Configuration = 'Release', [switch]$EnableLongRunning, [switch]$SkipBuild, [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { $EvidenceDirectory = Join-Path $repoRoot (Join-Path 'TestResults' ("OrchestrationAcceptance-{0:yyyyMMdd-HHmmss}" -f (Get-Date))) }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
Push-Location $repoRoot
try {
    $env:DOTNET_ANALYSIS_ORCHESTRATION_EVIDENCE = $EvidenceDirectory
    [ordered]@{ startedAtUtc = [DateTime]::UtcNow; commit = (& git rev-parse HEAD); os = [Environment]::OSVersion.VersionString; configuration = $Configuration; longRunning = [bool]$EnableLongRunning } | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'metadata.json') -Encoding utf8
    if (-not $SkipBuild) {
        & dotnet build .\DotnetAnalysis.sln --configuration $Configuration --property:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }
    & dotnet test .\tests\DotnetAnalysis.Orchestration.Tests\DotnetAnalysis.Orchestration.Tests.csproj --configuration $Configuration --no-build --logger 'trx;LogFileName=orchestration-unit.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Orchestration unit tests failed.' }
    & dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration $Configuration --no-build --filter 'TestCategory=WindowsDiagnosticsIntegration&FullyQualifiedName~Orchestration&FullyQualifiedName!~OrchestrationPerformanceTests&FullyQualifiedName!~OrchestrationStressTests' --logger 'trx;LogFileName=orchestration-integration.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Orchestration integration tests failed.' }
    if ($EnableLongRunning -and $env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_PERFORMANCE -eq 'true') {
        & dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration $Configuration --no-build --filter 'FullyQualifiedName~OrchestrationPerformanceTests' --logger 'trx;LogFileName=orchestration-performance.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Orchestration performance gate failed.' }
    }
    if ($EnableLongRunning -and $env:DOTNET_ANALYSIS_RUN_ORCHESTRATION_STRESS -eq 'true') {
        & dotnet test .\tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj --configuration $Configuration --no-build --filter 'FullyQualifiedName~OrchestrationStressTests' --logger 'trx;LogFileName=orchestration-stress.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Orchestration stress gate failed.' }
    }
    [ordered]@{ completedAtUtc = [DateTime]::UtcNow; status = 'passed'; evidenceDirectory = $EvidenceDirectory } | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'summary.json') -Encoding utf8
}
catch {
    [ordered]@{ completedAtUtc = [DateTime]::UtcNow; status = 'failed'; error = $_.Exception.ToString(); evidenceDirectory = $EvidenceDirectory } | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'summary.json') -Encoding utf8
    throw
}
finally {
    Remove-Item Env:DOTNET_ANALYSIS_ORCHESTRATION_EVIDENCE -ErrorAction SilentlyContinue
    Pop-Location
}
