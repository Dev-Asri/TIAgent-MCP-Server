@echo off
setlocal
cd /d "%~dp0"
title TIAgent MCP Server

if not exist "%~dp0bin\Debug\net48\TIAgent MCP Server.exe" (
    echo ========================================================
    echo   TIAgent MCP Server.exe bulunamadi, proje derleniyor...
    echo ========================================================
    dotnet build "%~dp0TIAgentMCPServer.csproj" -c Debug
    if errorlevel 1 (
        echo.
        echo [HATA] Derleme basarisiz oldu!
        pause
        exit /b 1
    )
)

echo [BILGI] TIAgent MCP Server baslatiliyor...
cd /d "%~dp0bin\Debug\net48"
start "" "TIAgent MCP Server.exe"
exit /b 0
