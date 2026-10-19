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

if not exist "%ROOT%lib\Microsoft.Web.WebView2.Core.dll" (
  echo [ERROR] lib\ is incomplete. The WebView2 SDK must ship with the sources.
  pause
  exit /b 1
)

if not exist "dist" mkdir "dist"
if not exist "dist\tmp" mkdir "dist\tmp"

rem ---------- 1. app icon ----------
echo [1/5] Generating app icon ...
set "ICONOPT="
"%CSC%" /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\IconGen.exe" ^
  /reference:System.dll /reference:System.Drawing.dll ^
  "%ROOT%tools\IconGen.cs" "%ROOT%src\AppIcon.cs" >nul 2>&1
if not errorlevel 1 (
  "%ROOT%dist\tmp\IconGen.exe" "%ROOT%dist\tmp\app.ico" >nul 2>&1
  rem the path may contain spaces, so the quotes belong inside the variable
  if not errorlevel 1 set ICONOPT=/win32icon:"%ROOT%dist\tmp\app.ico"
)
if not defined ICONOPT echo       icon generation failed, falling back to default icon

rem ---------- 2. main program ----------
rem the raw exe is only an ingredient of the installer, it is not shipped on its own.
rem /platform:anycpu on purpose: one exe has to work on x64, x86 and ARM64.
rem the three WebView2Loader builds under lib\ cover the bitness difference.
echo [2/5] Compiling main program ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\tmp\AutoMicSwitch.exe" ^
  %ICONOPT% ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:"%ROOT%lib\Microsoft.Web.WebView2.Core.dll" ^
  /reference:"%ROOT%lib\Microsoft.Web.WebView2.WinForms.dll" ^
  /resource:"%ROOT%src\Dashboard.html",Dashboard.html ^
  "%ROOT%src\Program.cs" "%ROOT%src\AudioEngine.cs" "%ROOT%src\Updater.cs" ^
  "%ROOT%src\MainWindow.cs" "%ROOT%src\Theme.cs" "%ROOT%src\Json.cs" ^
  "%ROOT%src\AppIcon.cs" "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Main program build FAILED.
  pause
  exit /b 1
)

rem ---------- 3. staging ----------
rem a runnable copy of the installed layout, handy for testing without installing.
rem the runtimes\ tree must keep its shape: WebView2 picks the loader by process arch.
echo [3/5] Staging payload ...
if exist "dist\payload" rd /s /q "dist\payload"
mkdir "dist\payload"
mkdir "dist\payload\runtimes\win-x86\native"
mkdir "dist\payload\runtimes\win-x64\native"
mkdir "dist\payload\runtimes\win-arm64\native"
copy /y "%ROOT%dist\tmp\AutoMicSwitch.exe" "dist\payload\AutoMicSwitch.exe" >nul
copy /y "%ROOT%lib\Microsoft.Web.WebView2.Core.dll" "dist\payload\" >nul
copy /y "%ROOT%lib\Microsoft.Web.WebView2.WinForms.dll" "dist\payload\" >nul
copy /y "%ROOT%lib\runtimes\win-x86\native\WebView2Loader.dll" "dist\payload\runtimes\win-x86\native\" >nul
copy /y "%ROOT%lib\runtimes\win-x64\native\WebView2Loader.dll" "dist\payload\runtimes\win-x64\native\" >nul
copy /y "%ROOT%lib\runtimes\win-arm64\native\WebView2Loader.dll" "dist\payload\runtimes\win-arm64\native\" >nul

rem ---------- 4. installer ----------
rem every payload file is embedded, the installer unpacks the whole set.
rem resource names cannot contain a backslash, so the paths are flattened here
rem and rebuilt in Setup.cs.
echo [4/5] Compiling installer ...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:"%ROOT%dist\AutoMicSwitch-Setup.exe" ^
  %ICONOPT% ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /resource:"%ROOT%dist\tmp\AutoMicSwitch.exe",payload_AutoMicSwitch.exe ^
  /resource:"%ROOT%lib\Microsoft.Web.WebView2.Core.dll",payload_Microsoft.Web.WebView2.Core.dll ^
  /resource:"%ROOT%lib\Microsoft.Web.WebView2.WinForms.dll",payload_Microsoft.Web.WebView2.WinForms.dll ^
  /resource:"%ROOT%lib\runtimes\win-x86\native\WebView2Loader.dll",payload_WebView2Loader.x86.dll ^
  /resource:"%ROOT%lib\runtimes\win-x64\native\WebView2Loader.dll",payload_WebView2Loader.x64.dll ^
  /resource:"%ROOT%lib\runtimes\win-arm64\native\WebView2Loader.dll",payload_WebView2Loader.arm64.dll ^
  /resource:"%ROOT%rules.example.txt",payload_rules.example.txt ^
  "%ROOT%src\Setup.cs" "%ROOT%src\AssemblyInfo.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Installer build FAILED.
  pause
  exit /b 1
)

echo [5/5] Done.
echo.
echo   dist\AutoMicSwitch-Setup.exe   installer, the only artifact to ship
echo   dist\payload\                  same layout, runnable in place for testing
echo.
echo Installs to: %%LOCALAPPDATA%%\AutoMicSwitch
echo Run pack-release.cmd to also generate dist\latest.yml
echo.
pause
