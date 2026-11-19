@echo off
chcp 65001 >nul
cd /d "%~dp0"
title Isabelle - instalator

where dotnet >nul 2>nul
if errorlevel 1 (
    echo Brakuje .NET SDK - uruchom najpierw build.bat
    pause
    exit /b 1
)

echo Tworze instalator Isabelle-Setup.exe...
taskkill /im Isabelle.exe /f >nul 2>nul
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish > installer_log.txt 2>&1
if errorlevel 1 goto :fail

if not exist installer mkdir installer
copy /y "publish\Isabelle.exe" "installer\Isabelle-Setup.exe" >nul
if errorlevel 1 goto :fail

echo.
echo Gotowe: installer\Isabelle-Setup.exe
explorer installer
exit /b

:fail
echo.
echo Cos poszlo nie tak - szczegoly w pliku installer_log.txt
pause
exit /b 1
