@echo off
REM Erstellt Systemprotokoll.exe mit dem in Windows eingebauten C#-Compiler.
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework 4 wurde nicht gefunden. Bitte Systemprotokoll_starten.bat verwenden.
    pause
    exit /b 1
)
if not exist "Systemprotokoll.ps1" (
    echo Systemprotokoll.ps1 fehlt im selben Ordner.
    pause
    exit /b 1
)
echo Erstelle Systemprotokoll.exe ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:Systemprotokoll.exe /win32manifest:app.manifest /resource:Systemprotokoll.ps1,Systemprotokoll.ps1 Launcher.cs
if errorlevel 1 (
    echo.
    echo Beim Erstellen ist ein Fehler aufgetreten - siehe Meldung oben.
    pause
    exit /b 1
)
echo.
echo Fertig: %~dp0Systemprotokoll.exe
echo Die EXE enthaelt alles und kann allein weitergegeben oder auf einen USB-Stick kopiert werden.
pause
