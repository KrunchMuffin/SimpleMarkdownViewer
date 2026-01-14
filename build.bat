@echo off
cd /d F:\SimpleMarkdownViewer
"C:\Program Files\dotnet\dotnet.exe" restore
"C:\Program Files\dotnet\dotnet.exe" build --verbosity minimal
if %ERRORLEVEL% EQU 0 (
    echo.
    echo BUILD SUCCEEDED
    echo.
    dir /s /b *.exe 2>nul | findstr /i "bin"
) else (
    echo BUILD FAILED
)
pause
