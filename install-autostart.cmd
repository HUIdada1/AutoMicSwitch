@echo off
setlocal
title AutoMicSwitch - Install Autostart
set "EXE=%~dp0dist\AutoMicSwitch.exe"

echo ==============================================
echo   AutoMicSwitch 2.0  -  Install Autostart
echo ==============================================
echo.

if not exist "%EXE%" (
  echo [ERROR] Not found: %EXE%
  echo         Please run build.cmd first.
  pause
  exit /b 1
)

echo [1/3] Stopping old instance ...
taskkill /f /im AutoMicSwitch.exe >nul 2>&1

echo [2/3] Writing autostart registry entry ...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AutoMicSwitch /t REG_SZ /d "\"%EXE%\" --startup" /f >nul
if errorlevel 1 (
  echo [ERROR] Failed to write registry.
  pause
  exit /b 1
)

echo [3/3] Launching now ...
start "" "%EXE%"

echo.
echo [OK] Autostart enabled.
echo      Dashboard: http://127.0.0.1:17817/
echo.
pause
