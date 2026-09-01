@echo off
chcp 65001 >nul
set "APP_DIR=%~dp0dist\JustFloat Studio"
set "APP_EXE=%APP_DIR%\JustFloat Studio.exe"

echo ========================================
echo   JustFloat Studio
echo ========================================

if not exist "%APP_EXE%" (
    echo 未找到发布程序：%APP_EXE%
    echo 请先运行 dotnet publish。
    pause
    exit /b 1
)

start "JustFloat Studio" "%APP_EXE%"
