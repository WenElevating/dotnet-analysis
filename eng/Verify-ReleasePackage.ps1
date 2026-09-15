param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$EvidencePath = (Join-Path (Get-Location) 'TestResults\ReleaseAcceptance')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path $PackagePath).Path
if (-not (Test-Path $EvidencePath)) { New-Item -ItemType Directory -Path $EvidencePath -Force | Out-Null }
$failures = [System.Collections.Generic.List[string]]::new()
$required = @('DotnetAnalysis.Desktop.exe','DotnetAnalysis.Desktop.dll','DotnetAnalysis.Desktop.runtimeconfig.json','native\\DotnetAnalysis.RetentionProfiler.dll','native\\DotnetAnalysis.RetentionProfilerController.dll','release-manifest.json')
foreach ($name in $required) { if (-not (Test-Path (Join-Path $package $name))) { $failures.Add("Missing required file: $name") } }
Get-ChildItem $package -File -Recurse | Where-Object { $_.Extension -in '.pdb','.dbg' } | ForEach-Object { $failures.Add("Unexpected debug artifact: $($_.FullName)") }

function Get-PeMachine([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $null }
    $offset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($offset -lt 0 -or $offset + 6 -gt $bytes.Length) { return $null }
    return [BitConverter]::ToUInt16($bytes, $offset + 4)
}
foreach ($name in @('DotnetAnalysis.Desktop.exe','native\\DotnetAnalysis.RetentionProfiler.dll','native\\DotnetAnalysis.RetentionProfilerController.dll')) {
    $machine = Get-PeMachine (Join-Path $package $name)
    if ($machine -ne 0x8664) { $failures.Add("$name is not x64 PE (machine=$machine)") }
}
$manifest = Get-Content (Join-Path $package 'release-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.Files) {
    $file = Join-Path $package $entry.Path
    if (-not (Test-Path $file)) { $failures.Add("Manifest file missing: $($entry.Path)"); continue }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($actual -ne $entry.SHA256) { $failures.Add("Hash mismatch: $($entry.Path)") }
}
$result = [ordered]@{ Package = $package; VerifiedAtUtc = [DateTime]::UtcNow.ToString('O'); Passed = ($failures.Count -eq 0); Failures = $failures; FileCount = @(Get-ChildItem $package -File -Recurse).Count }
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path (Resolve-Path $EvidencePath).Path 'release-package-verification.json') -Encoding UTF8
if ($failures.Count) { $failures | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Output "Release package verification passed: $package"
