$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

$isWindows = $env:OS -eq 'Windows_NT'
if (-not $isWindows) { $failures.Add('Windows 10/11 is required.') }
if (-not [Environment]::Is64BitOperatingSystem) { $failures.Add('A 64-bit operating system is required.') }
if ($isWindows -and [Environment]::OSVersion.Version.Build -lt 19045) {
    $failures.Add("Windows build $([Environment]::OSVersion.Version.Build) is below the Windows 10 22H2 baseline (19045).")
}

$sdk = (& dotnet --version 2>$null).Trim()
if ($sdk -ne '10.0.303') { $failures.Add("Required SDK 10.0.303 is missing (found '$sdk').") }

$runtimes = (& dotnet --list-runtimes 2>$null) -join "`n"
foreach ($major in 8, 9, 10) {
    if ($runtimes -notmatch "Microsoft\.NETCore\.App\s+$major\.") {
        $failures.Add("Microsoft.NETCore.App $major.x runtime is missing.")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    exit 1
}

Write-Output 'Diagnostics integration prerequisites passed.'
exit 0
