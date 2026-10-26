#Requires -Version 5.1
<#
.SYNOPSIS
    Systemzustands-Protokoll fuer Windows (vor Abgabe zur Reparatur).

.DESCRIPTION
    Sammelt Hardware-, E/A- und kritische Windows-Fehler, bewertet sie und erzeugt
    einen Bericht (HTML + PDF), CSV-Exporte, Original-Ereignisprotokolle (.evtx)
    sowie SHA256-Pruefsummen und ein ZIP-Archiv als Nachweis.

    Es werden KEINE Aenderungen am System vorgenommen (nur lesende Pruefungen).

.PARAMETER Tage
    Zeitraum der Ereignisprotokoll-Auswertung in Tagen (Standard: 30).

.PARAMETER Ziel
    Ordner, in dem das Protokoll abgelegt wird (Standard: Desktop).

.PARAMETER Schnell
    Ueberspringt die laengeren Pruefungen (SFC und Dateisystem-Scan).
#>
[CmdletBinding()]
param(
    [int]$Tage = 30,
    [string]$Ziel = [Environment]::GetFolderPath('Desktop'),
    [switch]$Schnell
)

# ---------------------------------------------------------------------------
# Administratorrechte sicherstellen
# ---------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Starte mit Administratorrechten neu ...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Tage', $Tage, '-Ziel', "`"$Ziel`"")
    if ($Schnell) { $argList += '-Schnell' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'
try { $Host.UI.RawUI.WindowTitle = 'Systemzustands-Protokoll' } catch {}

$jetzt     = Get-Date
$stempel   = $jetzt.ToString('yyyy-MM-dd_HH-mm-ss')
$ordner    = Join-Path $Ziel ('Systemprotokoll_{0}_{1}' -f $env:COMPUTERNAME, $stempel)
$rohOrdner = Join-Path $ordner 'Rohdaten'
New-Item -ItemType Directory -Path $rohOrdner -Force | Out-Null
$startZeit = $jetzt.AddDays(-$Tage)

Write-Host ''
Write-Host '=====================================================' -ForegroundColor Green
Write-Host '   Systemzustands-Protokoll' -ForegroundColor Green
Write-Host "   Zeitraum: letzte $Tage Tage  |  Ausgabe: $ordner" -ForegroundColor Green
Write-Host '=====================================================' -ForegroundColor Green
Write-Host ''

# ---------------------------------------------------------------------------
# Hilfsfunktionen
# ---------------------------------------------------------------------------
$Befunde    = New-Object System.Collections.Generic.List[object]
$Abschnitte = New-Object System.Collections.Generic.List[string]

function Add-Befund {
    param([string]$Bereich, [ValidateSet('OK', 'HINWEIS', 'WARNUNG', 'KRITISCH')][string]$Status, [string]$Details)
    $Befunde.Add([pscustomobject]@{ Status = $Status; Bereich = $Bereich; Details = $Details })
}

function Schritt([string]$Text) { Write-Host "[*] $Text" -ForegroundColor Cyan }

function Kurz([string]$s, [int]$n = 400) {
    if (-not $s) { return '' }
    $s = ($s -replace '\s+', ' ').Trim()
    if ($s.Length -gt $n) { return $s.Substring(0, $n) + ' ...' }
    return $s
}

function HtmlEnc([string]$s) { return [System.Net.WebUtility]::HtmlEncode($s) }

function Add-Abschnitt {
    param(
        [string]$Titel,
        $Daten,
        [string]$Leer = 'Keine Eintraege gefunden - unauffaellig.',
        [string]$Text,
        [string]$Info,
        [switch]$Liste
    )
    $html = "<section><h2>$(HtmlEnc $Titel)</h2>"
    if ($Info) { $html += "<p class='info'>$(HtmlEnc $Info)</p>" }
    $arr = @($Daten | Where-Object { $null -ne $_ })
    if ($arr.Count -gt 0) {
        $as = 'Table'; if ($Liste) { $as = 'List' }
        $html += ($arr | ConvertTo-Html -Fragment -As $as | Out-String)
    }
    elseif (-not $Text) {
        $html += "<p class='leer'>$(HtmlEnc $Leer)</p>"
    }
    if ($Text) { $html += "<pre>$(HtmlEnc $Text)</pre>" }
    $html += '</section>'
    $Abschnitte.Add($html)
}

# ---------------------------------------------------------------------------
# 1. Systeminformationen
# ---------------------------------------------------------------------------
Schritt 'Systeminformationen'
$cs   = Get-CimInstance Win32_ComputerSystem
$os   = Get-CimInstance Win32_OperatingSystem
$bios = Get-CimInstance Win32_BIOS
$bb   = Get-CimInstance Win32_BaseBoard
$cpu  = @(Get-CimInstance Win32_Processor)
$gpu  = @(Get-CimInstance Win32_VideoController)
$uptime = $jetzt - $os.LastBootUpTime

$aeltestesEreignis = $null
try { $aeltestesEreignis = (Get-WinEvent -LogName System -MaxEvents 1 -Oldest -ErrorAction Stop).TimeCreated } catch {}

$sys = [ordered]@{
    'Protokoll erstellt'               = $jetzt.ToString('dd.MM.yyyy HH:mm:ss') + ' (' + [TimeZoneInfo]::Local.Id + ')'
    'Ausgewerteter Zeitraum'           = $startZeit.ToString('dd.MM.yyyy') + ' bis ' + $jetzt.ToString('dd.MM.yyyy') + " ($Tage Tage)"
    'Systemprotokoll reicht zurueck bis' = $(if ($aeltestesEreignis) { $aeltestesEreignis.ToString('dd.MM.yyyy HH:mm') } else { 'unbekannt' })
    'Benutzer'                         = "$env:USERDOMAIN\$env:USERNAME"
    'Computername'                     = $env:COMPUTERNAME
    'Hersteller'                       = $cs.Manufacturer
    'Modell'                           = $cs.Model
    'Seriennummer (Geraet)'            = $bios.SerialNumber
    'Mainboard'                        = "$($bb.Manufacturer) $($bb.Product) (SN: $($bb.SerialNumber))"
    'BIOS/UEFI'                        = "$($bios.Manufacturer) $($bios.SMBIOSBIOSVersion) vom $(if ($bios.ReleaseDate) { $bios.ReleaseDate.ToString('dd.MM.yyyy') })"
    'Betriebssystem'                   = "$($os.Caption) (Version $($os.Version), Build $($os.BuildNumber), $($os.OSArchitecture))"
    'Windows installiert am'           = $(if ($os.InstallDate) { $os.InstallDate.ToString('dd.MM.yyyy HH:mm') })
    'Letzter Systemstart'              = $os.LastBootUpTime.ToString('dd.MM.yyyy HH:mm:ss')
    'Laufzeit seit Start'              = '{0} Tage {1} Std {2} Min' -f $uptime.Days, $uptime.Hours, $uptime.Minutes
    'Prozessor'                        = ($cpu | ForEach-Object { "$($_.Name.Trim()) ($($_.NumberOfCores) Kerne / $($_.NumberOfLogicalProcessors) Threads)" }) -join '; '
    'Arbeitsspeicher gesamt'           = '{0:N1} GB' -f ($cs.TotalPhysicalMemory / 1GB)
    'Grafik'                           = ($gpu | ForEach-Object { $_.Name }) -join '; '
}
$sysTab = $sys.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Eigenschaft = $_.Key; Wert = [string]$_.Value } }

$ramTab = Get-CimInstance Win32_PhysicalMemory | ForEach-Object {
    [pscustomobject]@{
        'Steckplatz'   = $_.DeviceLocator
        'Hersteller'   = $_.Manufacturer
        'Teilenummer'  = "$($_.PartNumber)".Trim()
        'Seriennummer' = "$($_.SerialNumber)".Trim()
        'Groesse (GB)' = [math]::Round($_.Capacity / 1GB, 1)
        'Takt (MHz)'   = $_.ConfiguredClockSpeed
    }
}

$gpuTab = $gpu | ForEach-Object {
    [pscustomobject]@{
        'Grafikkarte'   = $_.Name
        'Treiber'       = $_.DriverVersion
        'Treiberdatum'  = $(if ($_.DriverDate) { $_.DriverDate.ToString('dd.MM.yyyy') })
        'Status'        = $_.Status
        'Aufloesung'    = $(if ($_.CurrentHorizontalResolution) { "$($_.CurrentHorizontalResolution) x $($_.CurrentVerticalResolution)" })
    }
}
foreach ($g in $gpu) {
    if ($g.Status -and $g.Status -ne 'OK') { Add-Befund 'Grafik' 'WARNUNG' "$($g.Name): Status '$($g.Status)'" }
}

# ---------------------------------------------------------------------------
# 2. Datentraeger / SMART
# ---------------------------------------------------------------------------
Schritt 'Datentraeger und SMART-Status'
$pdisks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
$diskTab = foreach ($d in $pdisks) {
    $r = $null
    try { $r = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
    $name = $d.FriendlyName
    $zustand = "$($d.HealthStatus)"

    if ($zustand -eq 'Healthy') { Add-Befund 'Datentraeger' 'OK' "$name - Zustand: Healthy" }
    else { Add-Befund 'Datentraeger' 'KRITISCH' "$name - Zustand: '$zustand'" }

    if ($r) {
        if ($r.ReadErrorsUncorrected -gt 0 -or $r.WriteErrorsUncorrected -gt 0) {
            Add-Befund 'Datentraeger' 'KRITISCH' "$name - unkorrigierbare Lese-/Schreibfehler: $($r.ReadErrorsUncorrected) / $($r.WriteErrorsUncorrected)"
        }
        if ($r.Wear -ge 80) { Add-Befund 'Datentraeger' 'WARNUNG' "$name - Verschleiss $($r.Wear) %" }
        if ($r.Temperature -ge 70) { Add-Befund 'Datentraeger' 'WARNUNG' "$name - Temperatur $($r.Temperature) Grad C" }
    }

    [pscustomobject]@{
        'Laufwerk'                = $name
        'Seriennummer'            = "$($d.SerialNumber)".Trim()
        'Typ / Bus'               = "$($d.MediaType) / $($d.BusType)"
        'Groesse (GB)'            = [math]::Round($d.Size / 1GB)
        'Zustand'                 = $zustand
        'Betriebsstatus'          = ($d.OperationalStatus -join ', ')
        'Temp. (C)'               = $r.Temperature
        'Max. Temp. (C)'          = $r.TemperatureMax
        'Verschleiss (%)'         = $r.Wear
        'Lesefehler unkorr.'      = $r.ReadErrorsUncorrected
        'Schreibfehler unkorr.'   = $r.WriteErrorsUncorrected
        'Betriebsstunden'         = $r.PowerOnHours
        'Einschaltzyklen'         = $r.StartStopCycleCount
    }
}

$smart = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction SilentlyContinue)
$smartTab = foreach ($s in $smart) {
    if ($s.PredictFailure) { Add-Befund 'SMART' 'KRITISCH' "SMART sagt Ausfall voraus: $($s.InstanceName)" }
    [pscustomobject]@{
        'Instanz'              = $s.InstanceName
        'Ausfall vorhergesagt' = $(if ($s.PredictFailure) { 'JA' } else { 'nein' })
        'Grund-Code'           = $s.Reason
    }
}
if ($smart.Count -gt 0 -and -not ($smart | Where-Object PredictFailure)) {
    Add-Befund 'SMART' 'OK' "Keine SMART-Ausfallvorhersage ($($smart.Count) Laufwerk(e) abgefragt)"
}

# Volumes + Dateisystem-Scan (nur lesend)
$vols = @(Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | Sort-Object DriveLetter)
$volTab = foreach ($v in $vols) {
    $scan = 'nicht geprueft'
    if (-not $Schnell -and $v.FileSystem -eq 'NTFS' -and "$($v.DriveType)" -eq 'Fixed') {
        Schritt "Dateisystem-Pruefung $($v.DriveLetter): (nur lesend, kann einige Minuten dauern)"
        try {
            $scan = [string](Repair-Volume -DriveLetter $v.DriveLetter -Scan -ErrorAction Stop)
            if ($scan -eq 'NoErrorsFound') { Add-Befund 'Dateisystem' 'OK' "Laufwerk $($v.DriveLetter): - keine Fehler gefunden" }
            else { Add-Befund 'Dateisystem' 'WARNUNG' "Laufwerk $($v.DriveLetter): - Scan-Ergebnis: $scan" }
        }
        catch { $scan = 'Pruefung nicht moeglich: ' + $_.Exception.Message }
    }
    $freiProz = $null
    if ($v.Size -gt 0) {
        $freiProz = [math]::Round($v.SizeRemaining / $v.Size * 100, 1)
        if ($freiProz -lt 10 -and "$($v.DriveType)" -eq 'Fixed') { Add-Befund 'Speicherplatz' 'HINWEIS' "Laufwerk $($v.DriveLetter): nur $freiProz % frei" }
    }
    if ("$($v.HealthStatus)" -ne 'Healthy') { Add-Befund 'Volume' 'WARNUNG' "Laufwerk $($v.DriveLetter): Zustand '$($v.HealthStatus)'" }
    [pscustomobject]@{
        'Laufwerk'      = "$($v.DriveLetter):"
        'Bezeichnung'   = $v.FileSystemLabel
        'Dateisystem'   = $v.FileSystem
        'Typ'           = "$($v.DriveType)"
        'Zustand'       = "$($v.HealthStatus)"
        'Groesse (GB)'  = [math]::Round($v.Size / 1GB, 1)
        'Frei (GB)'     = [math]::Round($v.SizeRemaining / 1GB, 1)
        'Frei (%)'      = $freiProz
        'Dateisystem-Scan' = $scan
    }
}

# ---------------------------------------------------------------------------
# 3. Ereignisprotokolle: Hardware-, E/A- und kritische Fehler
# ---------------------------------------------------------------------------
Schritt "Ereignisprotokolle der letzten $Tage Tage"
$Pruefungen = @(
    @{ Kat = 'Datentraeger E/A';            Prov = 'disk';                                        Ids = @(7, 11, 15, 51, 52, 153, 154); Lvl = $null }
    @{ Kat = 'Dateisystem (NTFS)';          Prov = 'Ntfs';                                        Ids = $null; Lvl = @(1, 2) }
    @{ Kat = 'Dateisystem (NTFS)';          Prov = 'Microsoft-Windows-Ntfs';                      Ids = $null; Lvl = @(1, 2) }
    @{ Kat = 'Speichercontroller';          Prov = 'storahci';                                    Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'stornvme';                                    Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'atapi';                                       Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'iaStorA';                                     Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'iaStorAC';                                    Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'iaStorAVC';                                   Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Speichercontroller';          Prov = 'iaStorVD';                                    Ids = $null; Lvl = @(1, 2, 3) }
    @{ Kat = 'Hardwarefehler (WHEA)';       Prov = 'Microsoft-Windows-WHEA-Logger';               Ids = $null; Lvl = $null }
    @{ Kat = 'Bluescreen';                  Prov = 'Microsoft-Windows-WER-SystemErrorReporting';  Ids = @(1001); Lvl = $null }
    @{ Kat = 'Unerwarteter Neustart';       Prov = 'Microsoft-Windows-Kernel-Power';              Ids = @(41); Lvl = $null }
    @{ Kat = 'Unerwartetes Herunterfahren'; Prov = 'EventLog';                                    Ids = @(6008); Lvl = $null }
    @{ Kat = 'Grafiktreiber';               Prov = 'Display';                                     Ids = @(4101); Lvl = $null }
    @{ Kat = 'Grafiktreiber';               Prov = 'nvlddmkm';                                    Ids = $null; Lvl = @(1, 2) }
    @{ Kat = 'Grafiktreiber';               Prov = 'amdkmdag';                                    Ids = $null; Lvl = @(1, 2) }
    @{ Kat = 'Thermische Drosselung';       Prov = 'Microsoft-Windows-Kernel-Processor-Power';    Ids = @(37); Lvl = $null }
    @{ Kat = 'Arbeitsspeicher-Test';        Prov = 'Microsoft-Windows-MemoryDiagnostics-Results'; Ids = $null; Lvl = $null }
)

$Schwere = @{
    'Datentraeger E/A'            = 'KRITISCH'
    'Dateisystem (NTFS)'          = 'WARNUNG'
    'Speichercontroller'          = 'WARNUNG'
    'Bluescreen'                  = 'KRITISCH'
    'Unerwarteter Neustart'       = 'WARNUNG'
    'Unerwartetes Herunterfahren' = 'HINWEIS'
    'Grafiktreiber'               = 'WARNUNG'
    'Thermische Drosselung'       = 'HINWEIS'
}

$hwEvents = New-Object System.Collections.Generic.List[object]
foreach ($p in $Pruefungen) {
    $f = @{ LogName = 'System'; ProviderName = $p.Prov; StartTime = $startZeit }
    if ($p.Ids) { $f.Id = $p.Ids }
    if ($p.Lvl) { $f.Level = $p.Lvl }
    try {
        foreach ($e in (Get-WinEvent -FilterHashtable $f -ErrorAction Stop)) {
            $hwEvents.Add([pscustomobject]@{
                Zeit      = $e.TimeCreated
                Kategorie = $p.Kat
                Quelle    = $e.ProviderName
                ID        = $e.Id
                Stufe     = $e.LevelDisplayName
                Meldung   = (Kurz $e.Message)
                LevelNum  = [int]$e.Level
            })
        }
    }
    catch { }  # keine Treffer bzw. Quelle auf diesem System nicht vorhanden
}

foreach ($kat in ($Pruefungen | ForEach-Object { $_.Kat } | Select-Object -Unique)) {
    $treffer = @($hwEvents | Where-Object Kategorie -eq $kat)
    if ($kat -eq 'Arbeitsspeicher-Test') {
        if ($treffer.Count -eq 0) { Add-Befund $kat 'HINWEIS' 'Im Zeitraum wurde kein Windows-Speichertest durchgefuehrt' }
        elseif ($treffer | Where-Object { $_.LevelNum -in 1, 2 }) { Add-Befund $kat 'KRITISCH' 'Windows-Speichertest hat Fehler gemeldet' }
        else { Add-Befund $kat 'OK' "Windows-Speichertest ohne Fehler ($($treffer[0].Zeit))" }
        continue
    }
    if ($treffer.Count -eq 0) { Add-Befund $kat 'OK' 'Keine Eintraege im Zeitraum'; continue }

    $status = $Schwere[$kat]
    if ($kat -eq 'Hardwarefehler (WHEA)') {
        if ($treffer | Where-Object { $_.LevelNum -in 1, 2 }) { $status = 'KRITISCH' } else { $status = 'WARNUNG' }
    }
    $letzter = ($treffer | Sort-Object Zeit -Descending | Select-Object -First 1).Zeit
    Add-Befund $kat $status ("{0} Ereignis(se), zuletzt am {1}" -f $treffer.Count, $letzter.ToString('dd.MM.yyyy HH:mm'))
}
$hwTab = $hwEvents | Sort-Object Zeit -Descending | Select-Object * -ExcludeProperty LevelNum

# Alle kritischen Fehler und Fehler (System + Anwendung)
Schritt 'Allgemeine kritische Fehler und Fehler'
$sysErr = @(); $appErr = @()
try { $sysErr = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1, 2; StartTime = $startZeit } -ErrorAction Stop) } catch {}
try { $appErr = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2; StartTime = $startZeit } -ErrorAction Stop) } catch {}

function Zusammenfassen($events) {
    $events | Group-Object ProviderName, Id | Sort-Object Count -Descending | ForEach-Object {
        $l = $_.Group | Sort-Object TimeCreated -Descending | Select-Object -First 1
        [pscustomobject]@{
            'Anzahl'          = $_.Count
            'Quelle'          = $l.ProviderName
            'ID'              = $l.Id
            'Stufe'           = $l.LevelDisplayName
            'Zuletzt'         = $l.TimeCreated
            'Beispielmeldung' = (Kurz $l.Message 250)
        }
    }
}
$sysErrTab = Zusammenfassen $sysErr
$appErrTab = Zusammenfassen $appErr

$sysKrit = @($sysErr | Where-Object Level -eq 1).Count
if ($sysKrit -gt 0) { Add-Befund 'Systemprotokoll' 'WARNUNG' "$sysKrit kritische(s) Ereignis(se) im Systemprotokoll" }
else { Add-Befund 'Systemprotokoll' 'OK' 'Keine kritischen Ereignisse im Systemprotokoll' }
Add-Befund 'Systemprotokoll' 'HINWEIS' ("{0} Fehlereintraege (System) / {1} (Anwendungen) - Details im Bericht und in den CSV-Dateien" -f $sysErr.Count, $appErr.Count)

$csvFelder = @(
    @{ n = 'Zeit'; e = { $_.TimeCreated.ToString('dd.MM.yyyy HH:mm:ss') } },
    @{ n = 'Protokoll'; e = { $_.LogName } },
    @{ n = 'Quelle'; e = { $_.ProviderName } },
    @{ n = 'ID'; e = { $_.Id } },
    @{ n = 'Stufe'; e = { $_.LevelDisplayName } },
    @{ n = 'Meldung'; e = { ($_.Message -replace '\s+', ' ') } }
)
$sysErr | Select-Object $csvFelder | Export-Csv (Join-Path $ordner 'Fehler_System.csv') -NoTypeInformation -Encoding UTF8 -Delimiter ';'
$appErr | Select-Object $csvFelder | Export-Csv (Join-Path $ordner 'Fehler_Anwendungen.csv') -NoTypeInformation -Encoding UTF8 -Delimiter ';'
$hwTab  | Export-Csv (Join-Path $ordner 'Hardware_Ereignisse.csv') -NoTypeInformation -Encoding UTF8 -Delimiter ';'

# Original-Ereignisprotokolle sichern (unveraenderte Rohdaten)
Schritt 'Sichere Original-Ereignisprotokolle (.evtx)'
foreach ($log in 'System', 'Application') {
    & wevtutil.exe epl $log (Join-Path $rohOrdner "$log.evtx") 2>&1 | Out-Null
}

# ---------------------------------------------------------------------------
# 4. Geraete-Manager
# ---------------------------------------------------------------------------
Schritt 'Geraete-Manager'
$cmCodes = @{
    1 = 'Nicht korrekt konfiguriert'; 3 = 'Treiber beschaedigt'; 10 = 'Geraet kann nicht gestartet werden'
    12 = 'Nicht genuegend Ressourcen'; 14 = 'Neustart erforderlich'; 18 = 'Treiber neu installieren'
    19 = 'Registrierung fehlerhaft'; 22 = 'Deaktiviert'; 24 = 'Nicht vorhanden / fehlerhaft'
    28 = 'Kein Treiber installiert'; 29 = 'Von Firmware deaktiviert'; 31 = 'Treiber konnte nicht geladen werden'
    32 = 'Dienst deaktiviert'; 37 = 'Treiber-Initialisierung fehlgeschlagen'; 39 = 'Treiber fehlt / beschaedigt'
    43 = 'Geraet hat ein Problem gemeldet'; 52 = 'Treibersignatur ungueltig'
}
$devs = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -and $_.ConfigManagerErrorCode -notin 0, 45 })
$devTab = foreach ($d in $devs) {
    $code = [int]$d.ConfigManagerErrorCode
    $txt = $cmCodes[$code]; if (-not $txt) { $txt = 'Fehler' }
    if ($code -eq 22) { Add-Befund 'Geraete-Manager' 'HINWEIS' "$($d.Name): deaktiviert" }
    else { Add-Befund 'Geraete-Manager' 'WARNUNG' "$($d.Name): Code $code - $txt" }
    [pscustomobject]@{ 'Geraet' = $d.Name; 'Klasse' = $d.PNPClass; 'Code' = $code; 'Bedeutung' = $txt; 'Geraete-ID' = $d.PNPDeviceID }
}
if ($devs.Count -eq 0) { Add-Befund 'Geraete-Manager' 'OK' 'Alle Geraete ohne Fehlercode' }

# ---------------------------------------------------------------------------
# 5. Absturzabbilder (Bluescreens)
# ---------------------------------------------------------------------------
Schritt 'Absturzabbilder'
$dumps = @()
if (Test-Path "$env:SystemRoot\Minidump") { $dumps += @(Get-ChildItem "$env:SystemRoot\Minidump" -Filter *.dmp -ErrorAction SilentlyContinue) }
if (Test-Path "$env:SystemRoot\MEMORY.DMP") { $dumps += Get-Item "$env:SystemRoot\MEMORY.DMP" }
$dumpTab = $dumps | Sort-Object LastWriteTime -Descending | ForEach-Object {
    [pscustomobject]@{ 'Datei' = $_.FullName; 'Datum' = $_.LastWriteTime; 'Groesse (KB)' = [math]::Round($_.Length / 1KB) }
}
$dumpsNeu = @($dumps | Where-Object { $_.LastWriteTime -ge $startZeit })
if ($dumpsNeu.Count -gt 0) { Add-Befund 'Absturzabbilder' 'WARNUNG' "$($dumpsNeu.Count) Absturzabbild(er) im Zeitraum" }
elseif ($dumps.Count -gt 0) { Add-Befund 'Absturzabbilder' 'HINWEIS' "$($dumps.Count) aeltere(s) Absturzabbild(er) vor dem Zeitraum" }
else { Add-Befund 'Absturzabbilder' 'OK' 'Keine Absturzabbilder vorhanden' }

# ---------------------------------------------------------------------------
# 6. Zuverlaessigkeitsindex
# ---------------------------------------------------------------------------
Schritt 'Zuverlaessigkeitsindex'
$relTab = $null
try {
    $rel = @(Get-CimInstance Win32_ReliabilityStabilityMetrics -ErrorAction Stop | Sort-Object TimeGenerated -Descending)
    if ($rel.Count -gt 0) {
        $akt = [math]::Round($rel[0].SystemStabilityIndex, 2)
        $imZeitraum = @($rel | Where-Object { $_.TimeGenerated -ge $startZeit })
        $min = if ($imZeitraum.Count) { [math]::Round(($imZeitraum | Measure-Object SystemStabilityIndex -Minimum).Minimum, 2) } else { $akt }
        $relTab = [pscustomobject]@{ 'Aktueller Index (1-10)' = $akt; 'Minimum im Zeitraum' = $min; 'Stand vom' = $rel[0].TimeGenerated }
        if ($akt -ge 7) { Add-Befund 'Zuverlaessigkeit' 'OK' "Stabilitaetsindex $akt von 10" }
        else { Add-Befund 'Zuverlaessigkeit' 'HINWEIS' "Stabilitaetsindex $akt von 10 (sinkt auch durch Programmabstuerze)" }
    }
}
catch { }

# ---------------------------------------------------------------------------
# 7. Akku (Notebooks)
# ---------------------------------------------------------------------------
$akkuTab = $null
if (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue) {
    Schritt 'Akku'
    & powercfg.exe /batteryreport /output (Join-Path $ordner 'Akkubericht.html') 2>&1 | Out-Null
    $design = (Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1).DesignedCapacity
    $voll   = (Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1).FullChargedCapacity
    if ($design -gt 0 -and $voll -gt 0) {
        $proz = [math]::Round($voll / $design * 100, 1)
        $akkuTab = [pscustomobject]@{ 'Design-Kapazitaet (mWh)' = $design; 'Aktuelle Vollladung (mWh)' = $voll; 'Akkugesundheit (%)' = $proz }
        if ($proz -lt 60) { Add-Befund 'Akku' 'WARNUNG' "Akkugesundheit $proz %" } else { Add-Befund 'Akku' 'OK' "Akkugesundheit $proz %" }
    }
}

# ---------------------------------------------------------------------------
# 8. Temperatur (falls vom System bereitgestellt)
# ---------------------------------------------------------------------------
$tempTab = Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ 'Zone' = $_.InstanceName; 'Temperatur (C)' = [math]::Round($_.CurrentTemperature / 10 - 273.15, 1) }
}

# ---------------------------------------------------------------------------
# 9. Windows-Systemdateien
# ---------------------------------------------------------------------------
Schritt 'Windows-Komponentenspeicher (DISM, nur Pruefung)'
$dismOut = (& dism.exe /Online /Cleanup-Image /CheckHealth /English 2>&1 | Out-String).Trim()
if ($dismOut -match 'No component store corruption detected') { Add-Befund 'Windows-Integritaet' 'OK' 'DISM: Komponentenspeicher ohne Beschaedigung' }
elseif ($dismOut -match 'repairable|corrupt') { Add-Befund 'Windows-Integritaet' 'WARNUNG' 'DISM: Komponentenspeicher beschaedigt/reparierbar' }
else { Add-Befund 'Windows-Integritaet' 'HINWEIS' 'DISM: Ergebnis siehe Bericht' }

$sfcOut = ''
if (-not $Schnell) {
    Schritt 'Systemdatei-Pruefung (SFC /verifyonly, nur Pruefung - dauert einige Minuten)'
    $altEnc = $null
    try { $altEnc = [Console]::OutputEncoding; [Console]::OutputEncoding = [System.Text.Encoding]::Unicode } catch {}
    $sfcOut = (& sfc.exe /verifyonly 2>&1 | Out-String)
    try { if ($altEnc) { [Console]::OutputEncoding = $altEnc } } catch {}
    $sfcOut = ($sfcOut -replace "`0", '' -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '\d+\s*%' }) -join "`n"
    if ($sfcOut -match 'did not find any integrity violations|keine Integrit') { Add-Befund 'Windows-Integritaet' 'OK' 'SFC: keine Integritaetsverletzungen' }
    elseif ($sfcOut -match 'integrity violations|Integrit') { Add-Befund 'Windows-Integritaet' 'WARNUNG' 'SFC: Integritaetsverletzungen gefunden' }
    else { Add-Befund 'Windows-Integritaet' 'HINWEIS' 'SFC: Ergebnis siehe Bericht' }
}

# ---------------------------------------------------------------------------
# Bericht erstellen
# ---------------------------------------------------------------------------
Schritt 'Erstelle Bericht'
$rang = @{ 'KRITISCH' = 0; 'WARNUNG' = 1; 'HINWEIS' = 2; 'OK' = 3 }
$befundTab = $Befunde | Sort-Object @{ e = { $rang[$_.Status] } }, Bereich
$nKrit = @($Befunde | Where-Object Status -eq 'KRITISCH').Count
$nWarn = @($Befunde | Where-Object Status -eq 'WARNUNG').Count
$nHinw = @($Befunde | Where-Object Status -eq 'HINWEIS').Count
$nOk   = @($Befunde | Where-Object Status -eq 'OK').Count

if ($nKrit) { $gesamt = 'KRITISCHE BEFUNDE VORHANDEN'; $gesamtKlasse = 'krit' }
elseif ($nWarn) { $gesamt = 'WARNUNGEN VORHANDEN'; $gesamtKlasse = 'warn' }
else { $gesamt = 'UNAUFFAELLIG - keine Hardware- oder kritischen Fehler festgestellt'; $gesamtKlasse = 'ok' }

Add-Abschnitt -Titel 'Befunde im Ueberblick' -Daten $befundTab
Add-Abschnitt -Titel 'Systeminformationen' -Daten $sysTab
Add-Abschnitt -Titel 'Arbeitsspeicher-Module' -Daten $ramTab
Add-Abschnitt -Titel 'Grafik' -Daten $gpuTab
Add-Abschnitt -Titel 'Physische Datentraeger (Zustand und Zuverlaessigkeitszaehler)' -Daten $diskTab -Info 'Leere Felder bedeuten, dass das Laufwerk diesen Wert nicht an Windows meldet.'
Add-Abschnitt -Titel 'SMART-Ausfallvorhersage' -Daten $smartTab -Leer 'Keine SMART-Daten ueber WMI verfuegbar (bei NVMe-Laufwerken ueblich, siehe Zustand oben).'
Add-Abschnitt -Titel 'Laufwerke / Volumes' -Daten $volTab
Add-Abschnitt -Titel "Hardware-, E/A- und Absturz-Ereignisse (letzte $Tage Tage)" -Daten $hwTab -Leer 'Keine Hardware-, E/A- oder Absturz-Ereignisse im Zeitraum.' -Info 'Geprueft: Datentraeger-E/A, NTFS, Speichercontroller, WHEA-Hardwarefehler, Bluescreens, unerwartete Neustarts, Grafiktreiber-Resets, thermische Drosselung, Windows-Speichertest.'
Add-Abschnitt -Titel 'Geraete-Manager: Geraete mit Problemen' -Daten $devTab -Leer 'Alle Geraete arbeiten ohne Fehlercode.'
Add-Abschnitt -Titel 'Absturzabbilder (Bluescreen-Dumps)' -Daten $dumpTab -Leer 'Keine Absturzabbilder vorhanden.'
Add-Abschnitt -Titel 'Zuverlaessigkeitsindex' -Daten $relTab -Leer 'Nicht verfuegbar.'
if ($akkuTab) { Add-Abschnitt -Titel 'Akku' -Daten $akkuTab -Info 'Ausfuehrlicher Bericht: Akkubericht.html' }
if ($tempTab) { Add-Abschnitt -Titel 'Temperaturzonen (ACPI)' -Daten $tempTab }
Add-Abschnitt -Titel 'Windows-Komponentenspeicher (DISM /CheckHealth)' -Text $dismOut
if ($sfcOut) { Add-Abschnitt -Titel 'Systemdatei-Pruefung (SFC /verifyonly)' -Text $sfcOut }
Add-Abschnitt -Titel "Systemprotokoll: Fehler und kritische Fehler (zusammengefasst, $($sysErr.Count) Eintraege)" -Daten $sysErrTab -Info 'Vollstaendige Liste: Fehler_System.csv'
Add-Abschnitt -Titel "Anwendungsprotokoll: Fehler und kritische Fehler (zusammengefasst, $($appErr.Count) Eintraege)" -Daten $appErrTab -Info 'Vollstaendige Liste: Fehler_Anwendungen.csv'

$css = @'
:root{--ok:#1e7d32;--warn:#b26a00;--krit:#c62828;--hinw:#1565c0}
body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;background:#fff;font-size:13px}
h1{margin:0 0 4px;font-size:24px}
.sub{color:#555;margin-bottom:16px}
h2{font-size:16px;border-bottom:2px solid #ddd;padding-bottom:4px;margin-top:28px}
table{border-collapse:collapse;width:100%;margin:8px 0}
th,td{border:1px solid #ccc;padding:4px 6px;text-align:left;vertical-align:top}
th{background:#f0f0f0}
tr:nth-child(even) td{background:#fafafa}
pre{background:#f5f5f5;padding:8px;white-space:pre-wrap;font-size:12px}
.gesamt{padding:14px 18px;border-radius:6px;font-size:18px;font-weight:bold;color:#fff;margin:12px 0}
.gesamt.ok{background:var(--ok)}.gesamt.warn{background:var(--warn)}.gesamt.krit{background:var(--krit)}
.zahlen span{display:inline-block;margin-right:16px;font-weight:bold}
td.s-OK{color:var(--ok);font-weight:bold}td.s-WARNUNG{color:var(--warn);font-weight:bold}
td.s-KRITISCH{color:var(--krit);font-weight:bold}td.s-HINWEIS{color:var(--hinw);font-weight:bold}
.leer{color:var(--ok)}.info{color:#555;font-style:italic}
.unterschrift{display:flex;gap:40px;margin-top:48px}
.unterschrift div{flex:1;border-top:1px solid #333;padding-top:4px;font-size:12px}
.fuss{margin-top:32px;color:#555;font-size:11px}
@media print{body{margin:10mm}section{page-break-inside:auto}h2{page-break-after:avoid}}
'@

$html = @"
<!DOCTYPE html>
<html lang="de"><head><meta charset="utf-8">
<title>Systemzustand $(HtmlEnc $env:COMPUTERNAME) $($jetzt.ToString('dd.MM.yyyy'))</title>
<style>$css</style></head><body>
<h1>Systemzustands-Protokoll</h1>
<div class="sub">$(HtmlEnc "$($cs.Manufacturer) $($cs.Model)") &middot; Seriennummer: $(HtmlEnc $bios.SerialNumber) &middot; erstellt am $($jetzt.ToString('dd.MM.yyyy')) um $($jetzt.ToString('HH:mm:ss')) Uhr</div>
<div class="gesamt $gesamtKlasse">Gesamtbewertung: $(HtmlEnc $gesamt)</div>
<div class="zahlen"><span style="color:var(--krit)">Kritisch: $nKrit</span><span style="color:var(--warn)">Warnungen: $nWarn</span><span style="color:var(--hinw)">Hinweise: $nHinw</span><span style="color:var(--ok)">OK: $nOk</span></div>
$($Abschnitte -join "`n")
<div class="unterschrift"><div>Ort, Datum, Unterschrift Kunde</div><div>Ort, Datum, Unterschrift / Stempel Annahme Werkstatt</div></div>
<div class="fuss">Dieses Protokoll wurde automatisch und ausschliesslich lesend erstellt; am System wurden keine Aenderungen vorgenommen.
Die Original-Ereignisprotokolle (Rohdaten\*.evtx) sowie SHA256-Pruefsummen aller Dateien liegen dem Protokoll bei.</div>
</body></html>
"@

foreach ($st in 'OK', 'HINWEIS', 'WARNUNG', 'KRITISCH') {
    $html = $html.Replace("<td>$st</td>", "<td class=""s-$st"">$st</td>")
}
$berichtHtml = Join-Path $ordner 'Zustandsbericht.html'
$html | Out-File $berichtHtml -Encoding UTF8

# PDF ueber Microsoft Edge (headless) erzeugen, falls vorhanden
$berichtPdf = Join-Path $ordner 'Zustandsbericht.pdf'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) {
    Schritt 'Erstelle PDF'
    $tmpProfil = Join-Path $env:TEMP ('edgepdf_' + [guid]::NewGuid().ToString('N'))
    $fileUrl = ([Uri]$berichtHtml).AbsoluteUri
    try {
        $p = Start-Process -FilePath $edge -PassThru -WindowStyle Hidden -ArgumentList @(
            '--headless', '--disable-gpu', '--no-pdf-header-footer', "--user-data-dir=`"$tmpProfil`"",
            "--print-to-pdf=`"$berichtPdf`"", "`"$fileUrl`"")
        $p | Wait-Process -Timeout 90 -ErrorAction SilentlyContinue
    } catch {}
    Remove-Item $tmpProfil -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Pruefsummen + ZIP
# ---------------------------------------------------------------------------
Schritt 'Berechne SHA256-Pruefsummen'
$pruefDatei = Join-Path $ordner 'Pruefsummen_SHA256.txt'
$zeilen = @("SHA256-Pruefsummen - erstellt am $($jetzt.ToString('dd.MM.yyyy HH:mm:ss'))", '')
$zeilen += Get-ChildItem $ordner -Recurse -File | Sort-Object FullName | Get-FileHash -Algorithm SHA256 | ForEach-Object {
    '{0}  {1}' -f $_.Hash, $_.Path.Substring($ordner.Length + 1)
}
$zeilen | Out-File $pruefDatei -Encoding UTF8

$zip = "$ordner.zip"
Compress-Archive -Path (Join-Path $ordner '*') -DestinationPath $zip -Force
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash

# ---------------------------------------------------------------------------
# Abschluss
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=====================================================' -ForegroundColor Green
$farbe = 'Green'; if ($nKrit) { $farbe = 'Red' } elseif ($nWarn) { $farbe = 'Yellow' }
Write-Host " Gesamtbewertung: $gesamt" -ForegroundColor $farbe
Write-Host " Kritisch: $nKrit | Warnungen: $nWarn | Hinweise: $nHinw | OK: $nOk"
Write-Host ''
Write-Host " Ordner : $ordner"
Write-Host " Archiv : $zip"
Write-Host " SHA256 des Archivs:"
Write-Host "   $zipHash" -ForegroundColor White
Write-Host ''
Write-Host ' Tipp: Schicke dir das ZIP per E-Mail oder lade es in einen Cloud-Speicher hoch,'
Write-Host ' damit der Zeitpunkt der Erstellung unabhaengig belegt ist.'
Write-Host '=====================================================' -ForegroundColor Green

if (Test-Path $berichtPdf) { Start-Process $berichtPdf } else { Start-Process $berichtHtml }
Start-Process explorer.exe $ordner
Write-Host ''
Read-Host 'Enter druecken zum Beenden'
