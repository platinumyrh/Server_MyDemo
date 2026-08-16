@echo off
chcp 65001 >nul
echo ============================================
echo   Protobuf C# Code Generator
echo ============================================

set PROTO_DIR=Protos
set OUTPUT_DIR=GeneratedCodes
set PROTOC_EXE=E:\unity\Protoc\protoc-21.1-win64\bin\protoc.exe

echo.
echo [INFO] Proto文件目录: %PROTO_DIR%
echo [INFO] 输出目录: %OUTPUT_DIR%
echo [INFO] Protoc工具: %PROTOC_EXE%
echo.

if not exist "%OUTPUT_DIR%" (
    echo [INFO] 创建输出目录...
    mkdir "%OUTPUT_DIR%"
)

echo [INFO] 开始生成C#代码...
echo.

%PROTOC_EXE% ^
    --proto_path="%PROTO_DIR%" ^
    --csharp_out="%OUTPUT_DIR%" ^
    "%PROTO_DIR%\messages.proto"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ✅ 代码生成成功！
    echo 📁 输出位置: %OUTPUT_DIR%\Messages.cs
) else (
    echo.
    echo ❌ 代码生成失败！错误码: %ERRORLEVEL%
)

echo.
pause
