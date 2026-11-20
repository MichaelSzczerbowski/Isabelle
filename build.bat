@echo off
chcp 65001 >nul
cd /d "%~dp0"
title Isabelle - budowanie

where dotnet >nul 2>nul
if errorlevel 1 goto :install
dotnet --list-sdks | findstr /b "8. 9. 10." >nul
if errorlevel 1 goto :install
goto :build

:install
echo Brakuje .NET SDK - instaluje przez winget (moze potrwac kilka minut)...
winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
echo.
echo Gotowe. Zamknij to okno i uruchom build.bat jeszcze raz.
pause
exit /b

:build
rem najpierw grzecznie zamykamy Isabelle (bez martwych ikonek w zasobniku), potem ewentualnie sila
if exist "bin\Release\net8.0-windows10.0.19041.0\Isabelle.exe" start "" "bin\Release\net8.0-windows10.0.19041.0\Isabelle.exe" --quit
if exist "%LOCALAPPDATA%\Programs\Isabelle\Isabelle.exe" start "" "%LOCALAPPDATA%\Programs\Isabelle\Isabelle.exe" --quit
timeout /t 2 /nobreak >nul
taskkill /im Isabelle.exe /f >nul 2>nul
echo Buduje Isabelle...
dotnet build -c Release > build_log.txt 2>&1
if errorlevel 1 (
    echo.
    echo Budowanie nie powiodlo sie. Szczegoly sa w pliku build_log.txt
    pause
    exit /b 1
)
echo Uruchamiam...
start "" "bin\Release\net8.0-windows10.0.19041.0\Isabelle.exe"
