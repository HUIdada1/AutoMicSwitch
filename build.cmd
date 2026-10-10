@echo off
setlocal
title AutoMicSwitch - Build
cd /d "%~dp0"

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

echo ==============================================
echo   AutoMicSwitch - Build
echo ==============================================
echo.

if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
  pause
  exit /b 1
)

if not exist "dist" mkdir "dist"

echo [1/2] Compiling ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\AutoMicSwitch.exe" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /resource:"%ROOT%src\Dashboard.html",Dashboard.html ^
  "%ROOT%src\Program.cs" "%ROOT%src\AudioEngine.cs" "%ROOT%src\HttpServer.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Build FAILED.
  pause
  exit /b 1
)

if not exist "dist\rules.txt" copy /y "rules.example.txt" "dist\rules.txt" >nul

echo [2/2] Done.
echo.
echo [OK] dist\AutoMicSwitch.exe
echo.
echo Next: edit dist\rules.txt, then run dist\AutoMicSwitch.exe
echo Console: http://127.0.0.1:17817/
echo.
pause
