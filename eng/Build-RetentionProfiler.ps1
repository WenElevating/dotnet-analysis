[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$Configuration,
    [ValidateSet('x64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-VsWherePath {
    $command = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
    $candidates = @(
        if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
            Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        }
        if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
            Join-Path $programFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
        }
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Could not find vswhere.exe. Install Visual Studio Installer.'
}

function Import-VsDeveloperEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$VsDevCommand,
        [Parameter(Mandatory = $true)][string]$TargetArchitecture
    )

    $commandFile = Join-Path ([IO.Path]::GetTempPath()) ("dotnet-analysis-vsenv-{0}.cmd" -f [Guid]::NewGuid().ToString())
    try {
        $quotedVsDevCommand = [string]::Concat('"', $VsDevCommand, '"')
        $commandLines = @(
            "@echo off"
            ("call {0} -arch={1} -host_arch={1} -no_logo" -f $quotedVsDevCommand, $TargetArchitecture)
            "if errorlevel 1 exit /b %errorlevel%"
            "set"
        )
        Set-Content -LiteralPath $commandFile -Value $commandLines -Encoding ASCII

        $quotedCommandFile = [string]::Concat('"', $commandFile, '"')
        $environmentLines = & cmd.exe /d /s /c $quotedCommandFile
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to import the Visual Studio C++ environment (exit code $LASTEXITCODE)."
        }

        foreach ($line in $environmentLines) {
            if ($line -notmatch '^(?<Name>[^=]+)=(?<Value>.*)$') {
                continue
            }

            Set-Item -Path ("Env:{0}" -f $Matches.Name) -Value $Matches.Value
        }
    }
    finally {
        Remove-Item -LiteralPath $commandFile -Force -ErrorAction SilentlyContinue
    }
}

$sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
$buildPath = [IO.Path]::GetFullPath($BuildDirectory)
$vsWherePath = Resolve-VsWherePath

$installationLines = & $vsWherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
$vsWhereExitCode = $LASTEXITCODE
$installationPath = [string]($installationLines | Select-Object -First 1)
if ($vsWhereExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
    throw 'Could not find a Visual Studio instance with the MSVC x64 toolchain.'
}

$vsDevCommand = Join-Path $installationPath 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $vsDevCommand -PathType Leaf)) {
    throw "Visual Studio developer command script does not exist: $vsDevCommand"
}

Import-VsDeveloperEnvironment -VsDevCommand $vsDevCommand -TargetArchitecture $Architecture

$cmake = (Get-Command cmake.exe -ErrorAction Stop).Source
if ([string]::IsNullOrWhiteSpace($cmake)) {
    throw 'Could not find cmake.exe.'
}

New-Item -ItemType Directory -Path $buildPath -Force | Out-Null

$configureArguments = @(
    '-S', $sourcePath,
    '-B', $buildPath,
    '-G', 'NMake Makefiles',
    '-DBUILD_TESTING=OFF',
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY=$buildPath",
    "-DCMAKE_LIBRARY_OUTPUT_DIRECTORY=$buildPath"
)

& $cmake @configureArguments
if ($LASTEXITCODE -ne 0) {
    throw "Retention Profiler CMake configuration failed (exit code $LASTEXITCODE)."
}

& $cmake '--build' $buildPath
if ($LASTEXITCODE -ne 0) {
    throw "Retention Profiler CMake build failed (exit code $LASTEXITCODE)."
}
