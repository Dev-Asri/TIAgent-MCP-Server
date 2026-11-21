@echo off
setlocal
cd /d "%~dp0"
title TIAgent Template Creator

if not exist "%~dp0bin\Debug\net48\TIAgentTemplateCreator.exe" (
    echo ========================================================
    echo   TIAgentTemplateCreator.exe bulunamadi, derleniyor...
    echo ========================================================
    dotnet build "TIAgentTemplateCreator.csproj" -c Debug
    if errorlevel 1 (
        echo.
        echo [HATA] Derleme basarisiz oldu!
        pause
        exit /b 1
    )
)

echo [BILGI] TIAgent Template Creator baslatiliyor...
cd /d "%~dp0bin\Debug\net48"
start "" "TIAgentTemplateCreator.exe"
exit /b 0
