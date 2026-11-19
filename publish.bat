@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Tworze pojedynczy plik Isabelle.exe (nie wymaga instalowania .NET)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish > publish_log.txt 2>&1
if errorlevel 1 (
    echo Blad - szczegoly w publish_log.txt
    pause
    exit /b 1
)
echo Gotowe: folder "publish\Isabelle.exe"
explorer publish
