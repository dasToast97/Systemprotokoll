@echo off
REM Startet das Systemzustands-Protokoll (fragt nach Administratorrechten)
REM Optionen: Systemprotokoll_starten.bat -Tage 60      (laengerer Zeitraum)
REM           Systemprotokoll_starten.bat -Schnell      (ohne SFC und Dateisystem-Scan)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Systemprotokoll.ps1" %*
