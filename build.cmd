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
if not exist "dist\tmp" mkdir "dist\tmp"

rem ---------- 1. app icon ----------
echo [1/4] Generating app icon ...
set "ICONOPT="
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\IconGen.exe" ^
  /reference:System.dll /reference:System.Drawing.dll ^
  "%ROOT%tools\IconGen.cs" >nul 2>&1
if not errorlevel 1 (
  "%ROOT%dist\tmp\IconGen.exe" "%ROOT%dist\tmp\app.ico" >nul 2>&1
  if not errorlevel 1 set "ICONOPT=/win32icon:%ROOT%dist\tmp\app.ico"
)
if not defined ICONOPT echo       icon generation failed, falling back to default icon

rem ---------- 2. main program ----------
rem the raw exe is only an ingredient of the installer, it is not shipped on its own
echo [2/4] Compiling main program ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\AutoMicSwitch.exe" ^
  %ICONOPT% ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /resource:"%ROOT%src\Dashboard.html",Dashboard.html ^
  "%ROOT%src\Program.cs" "%ROOT%src\AudioEngine.cs" "%ROOT%src\HttpServer.cs" ^
  "%ROOT%src\Updater.cs" "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Main program build FAILED.
  pause
  exit /b 1
)

rem ---------- 3. installer ----------
echo [3/4] Compiling installer ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\AutoMicSwitch-Setup.exe" ^
  %ICONOPT% ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /resource:"%ROOT%dist\tmp\AutoMicSwitch.exe",AutoMicSwitch.exe ^
  /resource:"%ROOT%rules.example.txt",rules.example.txt ^
  "%ROOT%src\Setup.cs" "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Installer build FAILED.
  pause
  exit /b 1
)

echo [4/4] Done.
echo.
echo   dist\AutoMicSwitch-Setup.exe   installer, the only artifact to ship
echo.
echo Installs to: %%LOCALAPPDATA%%\AutoMicSwitch
echo Run pack-release.cmd to also generate dist\latest.yml
echo.
pause
