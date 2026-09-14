@echo off
setlocal
rem Double-click entry point: bootstrap PowerShell 7 and retain the result window.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-release.ps1" %*
set "packageExitCode=%errorlevel%"
echo.
if not "%packageExitCode%"=="0" echo Packaging failed. See the error above and build\package-logs for details.
pause
exit /b %packageExitCode%
