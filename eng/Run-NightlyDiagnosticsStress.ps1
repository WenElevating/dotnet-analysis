param(
    [string]$Configuration = 'Release',
    [string]$HeapObjectCounts = '5000000,10000000',
    [switch]$RunExtremeHeap,
    [switch]$RunResourceCycles,
    [switch]$RunExecutionBenchmark,
    [switch]$RunExecutionSoak,
    [switch]$RunRetentionBenchmark,
    [switch]$RunTransportProbe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$integrationProject = Join-Path $repoRoot 'tests\DotnetAnalysis.Diagnostics.IntegrationTests\DotnetAnalysis.Diagnostics.IntegrationTests.csproj'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$evidenceRoot = Join-Path $repoRoot "TestResults\NightlyDiagnosticsStress-$stamp"
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$logPath = Join-Path $evidenceRoot 'console.log'
Start-Transcript -LiteralPath $logPath -Force | Out-Null

function Invoke-GatedTest {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Filter,
        [hashtable]$Environment = @{}
    )

    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    $oldValues = @{}
    foreach ($key in $Environment.Keys) {
        $oldValues[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process')
    }
    try {
        & dotnet test $integrationProject --configuration $Configuration --no-build --filter $Filter --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
    }
    finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $oldValues[$key], 'Process')
        }
    }
}

try {
    Push-Location $repoRoot
    if ($RunExtremeHeap) {
        Invoke-GatedTest `
            -Name "Heap index stress ($HeapObjectCounts objects)" `
            -Filter 'FullyQualifiedName~HeapIndexStress_5MAnd10MHighDensityGraphs_PublishQueryableMappedArtifacts' `
            -Environment @{ DOTNET_ANALYSIS_RUN_HEAP_INDEX_STRESS = 'true'; DOTNET_ANALYSIS_HEAP_INDEX_STRESS_OBJECT_COUNTS = $HeapObjectCounts }
        Invoke-GatedTest `
            -Name 'Million-object EventPipe spool' `
            -Filter 'FullyQualifiedName~MillionObjectEventPipeSpool_PublishesMappedIndexWithoutResidentManagedGraph' `
            -Environment @{ DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_BENCHMARK = 'true' }
    }
    if ($RunResourceCycles) {
        Invoke-GatedTest `
            -Name 'Profiler detach resource cycles' `
            -Filter 'FullyQualifiedName~AttachAsync_AfterFiftyProfilerDetachCycles_TargetRemainsRunning'
    }
    if ($RunRetentionBenchmark) {
        Invoke-GatedTest `
            -Name 'Retention profiler large snapshot benchmark' `
            -Filter 'FullyQualifiedName~RetentionAnalysisLargeSnapshotBenchmark_RecordsCaptureAndRetentionQueryMeasurements' `
            -Environment @{ DOTNET_ANALYSIS_RUN_RETENTION_ANALYSIS_BENCHMARK = 'true' }
    }
    if ($RunTransportProbe) {
        Invoke-GatedTest `
            -Name 'Large snapshot transport probe' `
            -Filter 'FullyQualifiedName~LargeSnapshotTransportProbe_SeparatesEventPipeTransportFromLiveGraphConstruction' `
            -Environment @{ DOTNET_ANALYSIS_RUN_LARGE_SNAPSHOT_TRANSPORT_PROBE = 'true' }
    }
    if ($RunExecutionBenchmark) {
        Invoke-GatedTest `
            -Name 'Execution sampling 60-minute benchmark' `
            -Filter 'FullyQualifiedName~ExecutionSamplingBenchmark_ExplicitGate_MeetsSixtyMinuteHardThresholdsAndWritesRawEvidence' `
            -Environment @{ DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_BENCHMARK = 'true' }
    }
    if ($RunExecutionSoak) {
        Invoke-GatedTest `
            -Name 'Execution sampling 2-hour soak' `
            -Filter 'FullyQualifiedName~ExecutionSamplingStress_ExplicitGate_CompletesTwoHourSoakAndWritesRawEvidence' `
            -Environment @{ DOTNET_ANALYSIS_RUN_EXECUTION_PROFILE_STRESS = 'true' }
    }
    $summary = [ordered]@{
        StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        Configuration = $Configuration
        HeapObjectCounts = $HeapObjectCounts
        EvidenceDirectory = $evidenceRoot
        Completed = $true
    }
    $summary | ConvertTo-Json | Set-Content (Join-Path $evidenceRoot 'summary.json') -Encoding UTF8
    Write-Host "`nNightly diagnostics stress completed. Evidence: $evidenceRoot" -ForegroundColor Green
}
catch {
    $summary = [ordered]@{
        FailedAtUtc = [DateTime]::UtcNow.ToString('O')
        Configuration = $Configuration
        HeapObjectCounts = $HeapObjectCounts
        EvidenceDirectory = $evidenceRoot
        Completed = $false
        Error = $_.Exception.Message
    }
    $summary | ConvertTo-Json | Set-Content (Join-Path $evidenceRoot 'summary.json') -Encoding UTF8
    throw
}
finally {
    Pop-Location
    Stop-Transcript | Out-Null
}
