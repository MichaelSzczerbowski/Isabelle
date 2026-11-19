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

rem ---- klucz do podpisywania aktualizacji (tworzony tylko raz) ----
if not exist keys mkdir keys
if not exist "keys\private.xml" (
    echo Tworze klucz do podpisywania aktualizacji - zrob jego kopie zapasowa!
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$r = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072; [IO.File]::WriteAllText('keys\private.xml', $r.ToXmlString($true)); [IO.File]::WriteAllText('keys\public.xml', $r.ToXmlString($false))"
    if not exist "keys\private.xml" goto :fail
)

echo [1/2] Tworze Isabelle-Setup.exe...
taskkill /im Isabelle.exe /f >nul 2>nul
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish > installer_log.txt 2>&1
if errorlevel 1 goto :fail

if not exist installer mkdir installer
copy /y "publish\Isabelle.exe" "installer\Isabelle-Setup.exe" >nul
if errorlevel 1 goto :fail

echo [2/2] Podpisuje instalator...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$r = New-Object System.Security.Cryptography.RSACryptoServiceProvider; $r.FromXmlString([IO.File]::ReadAllText('keys\private.xml')); $d = [IO.File]::ReadAllBytes('installer\Isabelle-Setup.exe'); $s = $r.SignData($d, 'SHA256'); [IO.File]::WriteAllText('installer\Isabelle-Setup.exe.sig', [Convert]::ToBase64String($s))" >> installer_log.txt 2>&1
if not exist "installer\Isabelle-Setup.exe.sig" goto :fail

echo.
echo Gotowe:
echo   installer\Isabelle-Setup.exe
echo   installer\Isabelle-Setup.exe.sig
echo Do wydania na GitHubie dolacz OBA pliki.
explorer installer
exit /b

:fail
echo.
echo Cos poszlo nie tak - szczegoly w pliku installer_log.txt
pause
exit /b 1
