param(
    [string]$Version = "1.0.0",
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\artifacts\release"),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'DotnetAnalysis.sln'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageName = "DotnetAnalysis-$Version-win-x64"
if (-not (Test-Path $OutputRoot)) { New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null }
$packageRoot = Join-Path (Resolve-Path $OutputRoot).Path $packageName

if ($packageRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $packageRoot)) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        & dotnet build $solution --configuration Release --property:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "Release x64 build failed." }
    }

    & dotnet publish (Join-Path $repoRoot 'src\DotnetAnalysis.Desktop\DotnetAnalysis.Desktop.csproj') `
        --configuration Release --runtime win-x64 --self-contained true `
        --output $packageRoot --property:Platform=x64 --property:PublishReadyToRun=false `
        --property:DebugType=None --property:DebugSymbols=false --property:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Self-contained win-x64 publish failed." }

    $commit = (& git rev-parse HEAD).Trim()
    $files = Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName
    $hashes = foreach ($file in $files) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        [pscustomobject]@{ Path = $file.FullName.Substring($packageRoot.Length + 1); SHA256 = $hash; Length = $file.Length }
    }
    $metadata = [ordered]@{
        Product = 'DotnetAnalysis'
        Version = $Version
        Package = $packageName
        GitCommit = $commit
        BuiltAtUtc = [DateTime]::UtcNow.ToString('O')
        Support = [ordered]@{
            OS = 'Windows 10 22H2 (19045) and Windows 11 22H2 or later'
            Architecture = 'x64'
            Runtimes = @('.NET 8 CoreCLR', '.NET 9 CoreCLR', '.NET 10 CoreCLR')
            Unsupported = @('remote processes', 'Linux', 'containers', '.NET Framework', 'x86', '.dmp')
        }
        Files = $hashes
    }
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'release-manifest.json') -Encoding UTF8

    $acceptanceRoot = Join-Path $repoRoot "TestResults\ReleaseAcceptance-$stamp"
    New-Item -ItemType Directory -Path $acceptanceRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageRoot 'release-manifest.json') -Destination $acceptanceRoot
    & (Join-Path $PSScriptRoot 'Verify-ReleasePackage.ps1') -PackagePath $packageRoot -EvidencePath $acceptanceRoot
    if ($LASTEXITCODE -ne 0) { throw "Release package verification failed." }
    Write-Output "Release package: $packageRoot"
    Write-Output "Acceptance evidence: $acceptanceRoot"
}
finally { Pop-Location }
