@echo off
setlocal
title AutoMicSwitch - Uninstall Autostart

echo ==============================================
echo   AutoMicSwitch 2.0  -  Uninstall Autostart
echo ==============================================
echo.

echo [1/2] Stopping process ...
taskkill /f /im AutoMicSwitch.exe >nul 2>&1

echo [2/2] Removing autostart registry entry ...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AutoMicSwitch /f >nul 2>&1

echo.
echo [OK] Autostart removed. The program will no longer start with Windows.
echo.
pause
