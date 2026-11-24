@echo off
setlocal
where pwsh.exe >nul 2>nul
if errorlevel 1 (
    echo Install PowerShell 7 before building the Adapter.
    pause
    exit /b 1
)
pwsh.exe -NoLogo -NoProfile -File "%~dp0Build-Adapter.ps1"
set "MP_ADAPTER_BUILD_RESULT=%ERRORLEVEL%"
pause
exit /b %MP_ADAPTER_BUILD_RESULT%
