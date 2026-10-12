@echo off
setlocal
title AutoMicSwitch - Pack Release
cd /d "%~dp0"

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

echo ==============================================
echo   AutoMicSwitch - Pack Release
echo ==============================================
echo.

rem build.cmd ends with pause, feed it nul so it does not block here
call "%~dp0build.cmd" < nul
if errorlevel 1 (
  echo [ERROR] build failed, fix it before packing a release.
  pause
  exit /b 1
)

echo.
echo [pack] Generating latest.yml ...
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\PackRelease.exe" ^
  /reference:System.dll ^
  "%ROOT%tools\PackRelease.cs"
if errorlevel 1 (
  echo [ERROR] PackRelease build FAILED.
  pause
  exit /b 1
)

"%ROOT%dist\tmp\PackRelease.exe" "%ROOT%dist\AutoMicSwitch-Setup.exe" "%ROOT%dist\latest.yml" "%ROOT%release-notes.md"
if errorlevel 1 (
  echo [ERROR] latest.yml generation FAILED.
  pause
  exit /b 1
)

echo.
echo Upload these two files into the same GitHub Release:
echo   dist\AutoMicSwitch-Setup.exe   asset name must match exactly, the client fetches it by name
echo   dist\latest.yml                the client reads it for version and sha512
echo.
pause
