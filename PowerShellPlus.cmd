@echo off
setlocal
set "APP=%~dp0dist\PowerShellPlus.exe"

if not exist "%APP%" (
  echo PowerShellPlus 2 has not been built yet. Building and testing now...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 (
    echo.
    echo The build failed. Review the messages above.
    pause
    exit /b 1
  )
)

start "PowerShellPlus" "%APP%"
