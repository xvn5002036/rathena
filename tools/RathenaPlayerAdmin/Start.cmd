@echo off
setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1"

if errorlevel 1 (
  echo.
  echo Startup failed. Review the error message above.
  pause
)
