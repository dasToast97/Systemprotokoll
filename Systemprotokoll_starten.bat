@echo off
REM Startet das Systemzustands-Protokoll (fragt nach Administratorrechten)
REM Ohne Angaben erscheint ein Fenster zur Auswahl des Zeitraums.
REM Optionen: Systemprotokoll_starten.bat -Von 01.09.2026 -Bis "15.10.2026 18:00"
REM           Systemprotokoll_starten.bat -Von 01.09.2026 -Dauer 14
REM           Systemprotokoll_starten.bat -Tage 90 -Schnell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Systemprotokoll.ps1" %*
