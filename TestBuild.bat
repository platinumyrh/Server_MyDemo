@echo off
chcp 65001 >nul
echo ============================================
echo   Server Build Test
echo ============================================

cd /d "%~dp0"

echo.
echo [INFO] 正在恢复 NuGet 包...
dotnet restore

if %ERRORLEVEL% NEQ 0 (
    echo ❌ NuGet 包恢复失败！
    pause
    exit /b 1
)

echo.
echo [INFO] 正在编译项目...
dotnet build --no-restore

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ✅ 编译成功！
    echo.
    echo 🚀 运行命令: dotnet run
) else (
    echo.
    echo ❌ 编译失败！请检查上面的错误信息。
)

echo.
pause
