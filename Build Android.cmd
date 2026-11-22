@echo off
rem Builds the Android app (signed APK). See build\build-android.ps1 for details.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\build-android.ps1" %*
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
pause
