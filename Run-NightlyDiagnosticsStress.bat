@echo off
setlocal
cd /d "%~dp0"

echo ========================================
echo DotnetAnalysis nightly diagnostics stress
echo Started: %date% %time%
echo ========================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found in PATH.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\eng\Run-NightlyDiagnosticsStress.ps1" ^
    -RunExtremeHeap ^
    -RunResourceCycles ^
    -RunRetentionBenchmark ^
    -RunExecutionBenchmark ^
    -RunExecutionSoak

set "exitCode=%errorlevel%"
echo.
if "%exitCode%"=="0" (
    echo Nightly diagnostics stress completed successfully.
) else (
    echo Nightly diagnostics stress FAILED with exit code %exitCode%.
)
echo Evidence is under TestResults\NightlyDiagnosticsStress-*.
pause
exit /b %exitCode%
