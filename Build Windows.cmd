@echo off
rem Builds the Windows installer and portable app. See build\build-windows.ps1 for details.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\build-windows.ps1" %*
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
pause
