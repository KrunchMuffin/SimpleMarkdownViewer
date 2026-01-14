@echo off
echo ========================================
echo Building Simple Markdown Viewer Release
echo ========================================
echo.

cd /d %~dp0

echo [1/3] Cleaning previous build...
if exist publish rmdir /s /q publish
if exist installer rmdir /s /q installer

echo [2/3] Publishing application...
"C:\Program Files\dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: dotnet publish failed!
    pause
    exit /b 1
)

echo.
echo [3/3] Creating installer...
echo.

:: Try common Inno Setup locations
set ISCC=""
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "C:\Program Files\Inno Setup 6\ISCC.exe" set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"

if %ISCC%=="" (
    echo.
    echo Inno Setup not found!
    echo.
    echo Download from: https://jrsoftware.org/isdl.php
    echo.
    echo After installing, run this script again, or manually compile installer.iss
    echo.
    echo The published files are in the 'publish' folder - you can zip these for manual distribution.
    pause
    exit /b 0
)

%ISCC% installer.iss

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Inno Setup compilation failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo BUILD COMPLETE!
echo ========================================
echo.
echo Installer created: installer\SimpleMarkdownViewer-Setup-1.0.0.exe
echo.
pause
