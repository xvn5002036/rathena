@echo off
chcp 65001 >nul
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start.ps1"

if errorlevel 1 (
  echo.
  echo 啟動失敗，請查看上方錯誤訊息。
  pause
)
