@echo off
setlocal
cd /d "%~dp0"
title TIAgent MCP Server

if not exist "%~dp0bin\Debug\net48\TIAgent.exe" (
    echo ========================================================
    echo   TIAgent.exe bulunamadi, proje derleniyor...
    echo ========================================================
    dotnet build -c Debug
    if errorlevel 1 (
        echo.
        echo [HATA] Derleme basarisiz oldu!
        pause
        exit /b 1
    )
)

echo [BILGI] TIAgent MCP Server baslatiliyor...
cd /d "%~dp0bin\Debug\net48"
start "" "TIAgent.exe"
exit /b 0
