@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0eng\Run-OrchestrationAcceptance.ps1" %*
set EXITCODE=%ERRORLEVEL%
endlocal & exit /b %EXITCODE%