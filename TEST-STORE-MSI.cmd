@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0TEST-STORE-MSI.ps1"
if errorlevel 1 (
  echo.
  echo TEST FAILED. See the message above.
  pause
  exit /b 1
)
echo.
pause
