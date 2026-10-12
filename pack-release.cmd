@echo off
chcp 65001 >nul
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

rem build.cmd 结尾有 pause，重定向一下免得卡住
call "%~dp0build.cmd" < nul
if errorlevel 1 (
  echo [ERROR] 构建没过，先修好再来发版。
  pause
  exit /b 1
)

echo.
echo [pack] 生成 latest.yml ...
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\PackRelease.exe" ^
  /reference:System.dll ^
  "%ROOT%tools\PackRelease.cs"
if errorlevel 1 (
  echo [ERROR] 打包工具编译失败。
  pause
  exit /b 1
)

"%ROOT%dist\tmp\PackRelease.exe" "%ROOT%dist\AutoMicSwitch-Setup.exe" "%ROOT%dist\latest.yml" "%ROOT%release-notes.md"
if errorlevel 1 (
  echo [ERROR] latest.yml 生成失败。
  pause
  exit /b 1
)

echo.
echo 发版要传的就是这两个文件，放进同一个 Release：
echo   dist\AutoMicSwitch-Setup.exe   资产名必须叫这个，客户端按名字取
echo   dist\latest.yml                客户端靠它比版本、核 sha512
echo.
pause
