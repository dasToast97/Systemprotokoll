#Requires -Version 5.1
<#
.SYNOPSIS
    Reparatur-Modul des Systemzustands-Protokolls.

.DESCRIPTION
    Fuehrt ausgewaehlte Standard-Reparaturen mit Windows-Bordmitteln aus und protokolliert
    jede Aktion (Befehl, Start, Ende, Ergebnis, Ausgabe) als HTML und JSON.

    Normalerweise wird dieses Skript von Systemprotokoll.exe aufgerufen - dort laeuft vorher
    und nachher automatisch ein Zustandsprotokoll.

.PARAMETER Reparatur
    Komma-getrennte Liste: System, Datentraeger, Treiber, Update, Netzwerk, Komponenten, Druck, Zeit

.PARAMETER Wiederherstellungspunkt
    Vor den Reparaturen einen Systemwiederherstellungspunkt erstellen.

.PARAMETER Ziel
    Ordner, in dem der Unterordner Reparaturprotokoll_<Zeit> angelegt wird.
#>
[CmdletBinding()]
param(
    [string]$Reparatur = 'System,Datentraeger,Treiber',
    [switch]$Wiederherstellungspunkt,
    [string]$Ziel = [Environment]::GetFolderPath('Desktop')
)

$Gui = $env:SYSTEMPROTOKOLL_GUI -eq '1'
if ($Gui) { try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch {} }
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Fehler: Reparaturen benoetigen Administratorrechte.' -ForegroundColor Red
    exit 1
}

$jetzt   = Get-Date
$stempel = $jetzt.ToString('yyyy-MM-dd_HH-mm-ss')
$ordner  = Join-Path $Ziel "Reparaturprotokoll_$stempel"
New-Item -ItemType Directory -Path $ordner -Force | Out-Null

$auswahl = @($Reparatur -split '[,; ]+' | Where-Object { $_ } | ForEach-Object { $_.Trim().ToLowerInvariant() })
$Aktionen = New-Object System.Collections.Generic.List[object]
$script:Neustart = $false

function Schritt([string]$Text, [int]$Prozent = -1) {
    if ($Gui) { [Console]::Out.WriteLine("##P`t$Prozent`t$Text"); [Console]::Out.Flush() }
    else { Write-Host "[*] $Text" -ForegroundColor Cyan }
}

function Kurz([string]$s, [int]$n = 4000) {
    if (-not $s) { return '' }
    $s = $s.Trim()
    if ($s.Length -gt $n) { return $s.Substring(0, $n) + "`n ... (gekuerzt)" }
    return $s
}

function Add-Aktion {
    param([string]$Aktion, [datetime]$Start, [ValidateSet('OK', 'INFO', 'WARNUNG', 'FEHLER')][string]$Ergebnis,
          [string]$Details, [string]$Befehl = '', [string]$Ausgabe = '')
    $Aktionen.Add([pscustomobject]@{
        Aktion   = $Aktion
        Start    = $Start.ToString('dd.MM.yyyy HH:mm:ss')
        Ende     = (Get-Date).ToString('dd.MM.yyyy HH:mm:ss')
        Ergebnis = $Ergebnis
        Details  = $Details
        Befehl   = $Befehl
        Ausgabe  = (Kurz $Ausgabe)
    })
    $farbe = @{ OK = 'Green'; INFO = 'Gray'; WARNUNG = 'Yellow'; FEHLER = 'Red' }[$Ergebnis]
    if (-not $Gui) { Write-Host "    $Ergebnis - $Details" -ForegroundColor $farbe }
    else { [Console]::Out.WriteLine("    $Aktion -> $Ergebnis - $Details") }
}

# Liest eine Datei, die gerade von einem anderen Prozess geschrieben wird
function Read-Geteilt([string]$Pfad, [System.Text.Encoding]$Encoding) {
    try {
        $fs = [IO.File]::Open($Pfad, 'Open', 'Read', 'ReadWrite')
        try { $sr = New-Object IO.StreamReader($fs, $Encoding); return $sr.ReadToEnd() } finally { $fs.Dispose() }
    }
    catch { return '' }
}

# Startet ein Programm unsichtbar, zeigt dessen Prozentanzeige im Fortschritt an und liefert Exit-Code + Ausgabe
function Invoke-MitFortschritt {
    param([string]$Datei, [string]$Argumente, [string]$Text, [int]$Von, [int]$Bis, [switch]$Unicode)
    $out = Join-Path $env:TEMP ("rep_" + [guid]::NewGuid().ToString('N') + '.txt')
    $err = "$out.err"
    $enc = if ($Unicode) { [System.Text.Encoding]::Unicode } else { [System.Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage) }
    Schritt $Text $Von
    $p = Start-Process -FilePath $Datei -ArgumentList $Argumente -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $p.Handle   # sonst ist ExitCode spaeter leer
    $letzter = -1
    while (-not $p.HasExited) {
        Start-Sleep -Milliseconds 1500
        $txt = Read-Geteilt $out $enc
        $m = [regex]::Matches(($txt -replace "`0", ''), '(\d{1,3})(?:[.,]\d)?\s*%')
        if ($m.Count -gt 0) {
            $pct = [math]::Min(100, [int]$m[$m.Count - 1].Groups[1].Value)
            if ($pct -ne $letzter) {
                $letzter = $pct
                Schritt "$Text ($pct %)" ([int]($Von + ($Bis - $Von) * $pct / 100))
            }
        }
    }
    $p.WaitForExit()
    $ausgabe = (Read-Geteilt $out $enc) -replace "`0", ''
    $fehler = Read-Geteilt $err $enc
    Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    # Fortschrittszeilen aus der Ausgabe entfernen
    $ausgabe = ($ausgabe -split "[`r`n]+" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*\[?=*\s*\d{1,3}([.,]\d)?\s*%' -and $_ -notmatch '\d{1,3}\s*%\s*(abgeschlossen|complete)' }) -join "`n"
    if ($fehler.Trim()) { $ausgabe += "`n" + $fehler.Trim() }
    return [pscustomobject]@{ Code = $p.ExitCode; Ausgabe = $ausgabe.Trim() }
}

function Invoke-Kurz([string]$Datei, [string[]]$Argumente) {
    $a = (& $Datei $Argumente 2>&1 | Out-String).Trim()
    return [pscustomobject]@{ Code = $LASTEXITCODE; Ausgabe = $a }
}

# Fortschrittsbereiche je Reparatur (in Prozent dieser Phase)
$plan = [ordered]@{
    wiederherstellung = 4
    system            = 40
    datentraeger      = 10
    treiber           = 12
    komponenten       = 14
    update            = 6
    netzwerk          = 4
    druck             = 3
    zeit              = 3
}
$aktiv = @($plan.Keys | Where-Object { ($_ -eq 'wiederherstellung' -and $Wiederherstellungspunkt) -or $auswahl -contains $_ })
$gesamtGewicht = ($aktiv | ForEach-Object { $plan[$_] } | Measure-Object -Sum).Sum
if (-not $gesamtGewicht) { $gesamtGewicht = 1 }
$script:pos = 0
function Bereich([string]$Name) {
    $von = [int](100 * $script:pos / $gesamtGewicht)
    $script:pos += $plan[$Name]
    $bis = [int](100 * $script:pos / $gesamtGewicht)
    return @($von, $bis)
}

if (-not $Gui) {
    Write-Host ''
    Write-Host '=== Reparatur ===' -ForegroundColor Green
    Write-Host "Ausgewaehlt: $($auswahl -join ', ')"
}

# ---------------------------------------------------------------------------
# Wiederherstellungspunkt
# ---------------------------------------------------------------------------
if ($Wiederherstellungspunkt) {
    $b = Bereich 'wiederherstellung'
    Schritt 'Wiederherstellungspunkt erstellen' $b[0]
    $t0 = Get-Date
    $regPfad = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $alt = $null
    try { $alt = (Get-ItemProperty $regPfad -Name SystemRestorePointCreationFrequency -ErrorAction Stop).SystemRestorePointCreationFrequency } catch {}
    try {
        # Windows erlaubt sonst nur einen Punkt pro 24 Stunden - fuer diesen einen Aufruf aufheben
        Set-ItemProperty $regPfad -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
        $warn = $null
        Checkpoint-Computer -Description "Systemprotokoll: vor Reparatur $($jetzt.ToString('dd.MM.yyyy HH:mm'))" -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable warn -WarningAction SilentlyContinue
        if ($warn) { Add-Aktion 'Wiederherstellungspunkt' $t0 'WARNUNG' ("Nicht erstellt: " + ($warn -join ' ')) 'Checkpoint-Computer' }
        else { Add-Aktion 'Wiederherstellungspunkt' $t0 'OK' 'Wiederherstellungspunkt wurde erstellt' 'Checkpoint-Computer' }
    }
    catch {
        Add-Aktion 'Wiederherstellungspunkt' $t0 'WARNUNG' ("Nicht moeglich (Systemwiederherstellung evtl. deaktiviert): " + $_.Exception.Message) 'Checkpoint-Computer'
    }
    finally {
        if ($null -eq $alt) { Remove-ItemProperty $regPfad -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $regPfad -Name SystemRestorePointCreationFrequency -Value $alt -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------
# Windows-Systemdateien: DISM RestoreHealth + SFC scannow
# ---------------------------------------------------------------------------
if ($auswahl -contains 'system') {
    $b = Bereich 'system'
    $mitte = [int]($b[0] + ($b[1] - $b[0]) * 0.55)

    $t0 = Get-Date
    $r = Invoke-MitFortschritt 'dism.exe' '/Online /Cleanup-Image /RestoreHealth /English' 'Windows-Komponentenspeicher reparieren (DISM)' $b[0] $mitte
    if ($r.Code -eq 0) { Add-Aktion 'DISM RestoreHealth' $t0 'OK' 'Komponentenspeicher geprueft bzw. repariert' 'DISM /Online /Cleanup-Image /RestoreHealth' $r.Ausgabe }
    else { Add-Aktion 'DISM RestoreHealth' $t0 'FEHLER' "DISM beendet mit Code $($r.Code) (oft: keine Internetverbindung/Reparaturquelle)" 'DISM /Online /Cleanup-Image /RestoreHealth' $r.Ausgabe }

    $t0 = Get-Date
    $r = Invoke-MitFortschritt 'sfc.exe' '/scannow' 'Systemdateien prüfen und reparieren (SFC)' $mitte $b[1] -Unicode
    $a = $r.Ausgabe
    if ($a -match 'did not find any integrity violations|keine Integrit\S*verletzungen') {
        Add-Aktion 'SFC /scannow' $t0 'OK' 'Keine beschaedigten Systemdateien gefunden' 'sfc /scannow' $a
    }
    elseif ($a -match 'successfully repaired|erfolgreich repariert') {
        Add-Aktion 'SFC /scannow' $t0 'OK' 'Beschaedigte Systemdateien gefunden und repariert' 'sfc /scannow' $a
        if ($a -match 'reboot|restart|Neustart') { $script:Neustart = $true }
    }
    elseif ($a -match 'unable to fix|nicht reparieren|konnte .* nicht') {
        Add-Aktion 'SFC /scannow' $t0 'FEHLER' 'Beschaedigte Dateien gefunden, aber nicht alle repariert (siehe sfc_details.txt)' 'sfc /scannow' $a
    }
    elseif ($a -match 'pending|ausstehend') {
        Add-Aktion 'SFC /scannow' $t0 'WARNUNG' 'Eine Systemreparatur steht aus - Neustart erforderlich, danach erneut ausfuehren' 'sfc /scannow' $a
        $script:Neustart = $true
    }
    else { Add-Aktion 'SFC /scannow' $t0 'WARNUNG' "Ergebnis nicht eindeutig (Code $($r.Code))" 'sfc /scannow' $a }

    # Details aus dem CBS-Protokoll sichern
    try {
        $cbs = Join-Path $env:windir 'Logs\CBS\CBS.log'
        if (Test-Path $cbs) {
            Select-String -Path $cbs -Pattern '\[SR\]' -ErrorAction Stop |
                ForEach-Object { $_.Line } | Select-Object -Last 3000 |
                Out-File (Join-Path $ordner 'sfc_details.txt') -Encoding UTF8
        }
    } catch {}
}

# ---------------------------------------------------------------------------
# Datentraeger / Dateisystem
# ---------------------------------------------------------------------------
if ($auswahl -contains 'datentraeger') {
    $b = Bereich 'datentraeger'
    $vols = @(Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and $_.FileSystem -eq 'NTFS' -and "$($_.DriveType)" -eq 'Fixed' } | Sort-Object DriveLetter)
    # Nur fuer automatische Tests: Laufwerke einschraenken
    if ($env:SYSTEMPROTOKOLL_TEST_LAUFWERKE) { $vols = @($vols | Where-Object { $env:SYSTEMPROTOKOLL_TEST_LAUFWERKE -match "$($_.DriveLetter)" }) }
    $sysLw = $env:SystemDrive.TrimEnd(':')
    $i = 0
    foreach ($v in $vols) {
        $lw = "$($v.DriveLetter)"
        Schritt "Dateisystem von Laufwerk ${lw}: prüfen und reparieren (kann dauern)" ([int]($b[0] + ($b[1] - $b[0]) * $i / [math]::Max($vols.Count, 1)))
        $i++
        $t0 = Get-Date
        try {
            $scan = [string](Repair-Volume -DriveLetter $lw -Scan -ErrorAction Stop)
            if ($scan -eq 'NoErrorsFound') {
                Add-Aktion "Dateisystem ${lw}:" $t0 'OK' 'Keine Fehler gefunden - keine Reparatur noetig' "Repair-Volume -DriveLetter $lw -Scan" $scan
                continue
            }
            if ($lw -eq $sysLw) {
                $o = (& fsutil.exe dirty set "${lw}:" 2>&1 | Out-String).Trim()
                $script:Neustart = $true
                Add-Aktion "Dateisystem ${lw}:" $t0 'WARNUNG' "Fehler gefunden ($scan). Systemlaufwerk: Reparatur (chkdsk) beim naechsten Neustart geplant" "Repair-Volume -Scan; fsutil dirty set ${lw}:" "$scan`n$o"
            }
            else {
                $fix = [string](Repair-Volume -DriveLetter $lw -SpotFix -ErrorAction Stop)
                $nach = [string](Repair-Volume -DriveLetter $lw -Scan -ErrorAction Stop)
                if ($nach -ne 'NoErrorsFound') {
                    $fix2 = [string](Repair-Volume -DriveLetter $lw -OfflineScanAndFix -ErrorAction Stop)
                    $nach = [string](Repair-Volume -DriveLetter $lw -Scan -ErrorAction Stop)
                    $fix = "$fix / $fix2"
                }
                if ($nach -eq 'NoErrorsFound') { Add-Aktion "Dateisystem ${lw}:" $t0 'OK' "Fehler gefunden und repariert ($scan -> $nach)" "Repair-Volume -SpotFix / -OfflineScanAndFix" $fix }
                else { Add-Aktion "Dateisystem ${lw}:" $t0 'FEHLER' "Fehler nach Reparatur weiterhin vorhanden ($nach)" "Repair-Volume -SpotFix / -OfflineScanAndFix" $fix }
            }
        }
        catch { Add-Aktion "Dateisystem ${lw}:" $t0 'FEHLER' ("Pruefung/Reparatur nicht moeglich: " + $_.Exception.Message) "Repair-Volume" }
    }
    if ($vols.Count -eq 0) { Add-Aktion 'Dateisystem' (Get-Date) 'INFO' 'Keine festen NTFS-Laufwerke gefunden' }
}

# ---------------------------------------------------------------------------
# Treiber / Problemgeraete
# ---------------------------------------------------------------------------
$cmCodes = @{
    1 = 'Nicht korrekt konfiguriert'; 3 = 'Treiber beschaedigt'; 10 = 'Kann nicht gestartet werden'; 12 = 'Nicht genuegend Ressourcen'
    14 = 'Neustart erforderlich'; 18 = 'Treiber neu installieren'; 19 = 'Registrierung fehlerhaft'; 24 = 'Nicht vorhanden/fehlerhaft'
    28 = 'Kein Treiber installiert'; 29 = 'Von Firmware deaktiviert'; 31 = 'Treiber nicht geladen'; 32 = 'Dienst deaktiviert'
    37 = 'Initialisierung fehlgeschlagen'; 39 = 'Treiber fehlt/beschaedigt'; 43 = 'Geraet meldet Problem'; 52 = 'Signatur ungueltig'
}
function Get-Problemgeraete {
    @(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.ConfigManagerErrorCode -and $_.ConfigManagerErrorCode -notin 0, 22, 45 })
}
function Get-Geraetecode([string]$Id) {
    $d = Get-CimInstance Win32_PnPEntity -Filter ("PNPDeviceID='{0}'" -f ($Id -replace '\\', '\\' -replace "'", "\'")) -ErrorAction SilentlyContinue
    if ($d) { return [int]$d.ConfigManagerErrorCode } else { return $null }
}
function Code-Text($c) { if ($null -eq $c) { 'nicht mehr vorhanden' } elseif ($c -eq 0) { 'OK' } else { "Code $c ($($cmCodes[[int]$c]))" } }

if ($auswahl -contains 'treiber') {
    $b = Bereich 'treiber'
    Schritt 'Problemgeräte suchen' $b[0]
    $t0 = Get-Date
    $vorher = Get-Problemgeraete
    $scan = Invoke-Kurz 'pnputil.exe' @('/scan-devices')
    Start-Sleep -Seconds 3
    if ($vorher.Count -eq 0) {
        Add-Aktion 'Treiber' $t0 'OK' 'Keine Geraete mit Problemen im Geraete-Manager' 'pnputil /scan-devices' $scan.Ausgabe
    }
    $i = 0
    foreach ($d in $vorher) {
        $i++
        $id = $d.PNPDeviceID; $name = if ($d.Name) { $d.Name } else { $id }
        $codeVorher = [int]$d.ConfigManagerErrorCode
        Schritt "Gerät reparieren: $name" ([int]($b[0] + ($b[1] - $b[0]) * $i / ($vorher.Count + 1)))
        $t0 = Get-Date
        $log = New-Object System.Text.StringBuilder
        $codeJetzt = Get-Geraetecode $id
        $befehle = @('pnputil /scan-devices')

        if ($codeJetzt -and $codeVorher -ne 28) {
            $r = Invoke-Kurz 'pnputil.exe' @('/restart-device', $id)
            [void]$log.AppendLine($r.Ausgabe)
            $befehle += 'pnputil /restart-device'
            if ($r.Ausgabe -match 'Invalid|Ungueltig|not recognized|nicht erkannt|usage|Syntax') {
                # aeltere Windows-Versionen: aus- und wieder einschalten
                try { Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; Start-Sleep 2; Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; $befehle += 'Disable/Enable-PnpDevice' }
                catch { [void]$log.AppendLine($_.Exception.Message) }
            }
            Start-Sleep -Seconds 3
            $codeJetzt = Get-Geraetecode $id
        }
        if ($codeJetzt) {
            # Geraet entfernen und neu erkennen lassen -> Windows installiert den Treiber neu
            $r = Invoke-Kurz 'pnputil.exe' @('/remove-device', $id)
            [void]$log.AppendLine($r.Ausgabe)
            Start-Sleep -Seconds 2
            $r = Invoke-Kurz 'pnputil.exe' @('/scan-devices')
            [void]$log.AppendLine($r.Ausgabe)
            $befehle += 'pnputil /remove-device', 'pnputil /scan-devices'
            Start-Sleep -Seconds 6
            $codeJetzt = Get-Geraetecode $id
        }

        $details = "$name`: $(Code-Text $codeVorher) -> $(Code-Text $codeJetzt)"
        if ($codeJetzt -eq 0) { Add-Aktion 'Treiber' $t0 'OK' "$details (behoben)" ($befehle -join '; ') $log.ToString() }
        elseif ($null -eq $codeJetzt) { Add-Aktion 'Treiber' $t0 'INFO' "$details (Geraet nicht mehr angeschlossen/erkannt)" ($befehle -join '; ') $log.ToString() }
        elseif ($codeJetzt -eq 28 -or $codeJetzt -eq 14) {
            if ($codeJetzt -eq 14) { $script:Neustart = $true }
            Add-Aktion 'Treiber' $t0 'WARNUNG' ("$details - " + $(if ($codeJetzt -eq 28) { 'Treiber vom Hersteller oder ueber Windows Update installieren' } else { 'Neustart erforderlich' })) ($befehle -join '; ') $log.ToString()
        }
        else { Add-Aktion 'Treiber' $t0 'FEHLER' "$details (nicht behoben)" ($befehle -join '; ') $log.ToString() }
    }
}

# ---------------------------------------------------------------------------
# Komponentenspeicher bereinigen
# ---------------------------------------------------------------------------
if ($auswahl -contains 'komponenten') {
    $b = Bereich 'komponenten'
    $t0 = Get-Date
    $r = Invoke-MitFortschritt 'dism.exe' '/Online /Cleanup-Image /StartComponentCleanup /English' 'Komponentenspeicher bereinigen (DISM)' $b[0] $b[1]
    if ($r.Code -eq 0) { Add-Aktion 'Komponentenspeicher' $t0 'OK' 'Veraltete Komponenten entfernt' 'DISM /Online /Cleanup-Image /StartComponentCleanup' $r.Ausgabe }
    else { Add-Aktion 'Komponentenspeicher' $t0 'FEHLER' "DISM beendet mit Code $($r.Code)" 'DISM /Online /Cleanup-Image /StartComponentCleanup' $r.Ausgabe }
}

# ---------------------------------------------------------------------------
# Windows Update zuruecksetzen
# ---------------------------------------------------------------------------
if ($auswahl -contains 'update') {
    $b = Bereich 'update'
    Schritt 'Windows Update zurücksetzen' $b[0]
    $t0 = Get-Date
    $log = New-Object System.Text.StringBuilder
    $dienste = 'wuauserv', 'bits', 'cryptsvc', 'msiserver'
    foreach ($s in $dienste) {
        try { Stop-Service -Name $s -Force -ErrorAction Stop; [void]$log.AppendLine("Dienst $s gestoppt") }
        catch { [void]$log.AppendLine("Dienst $s : $($_.Exception.Message)") }
    }
    Start-Sleep -Seconds 2
    $ok = $true
    foreach ($pfad in (Join-Path $env:windir 'SoftwareDistribution'), (Join-Path $env:windir 'System32\catroot2')) {
        if (Test-Path $pfad) {
            $neu = "$pfad.bak_$stempel"
            try { Rename-Item $pfad $neu -ErrorAction Stop; [void]$log.AppendLine("$pfad -> $neu") }
            catch { $ok = $false; [void]$log.AppendLine("Umbenennen fehlgeschlagen: $pfad - $($_.Exception.Message)") }
        }
    }
    foreach ($s in 'cryptsvc', 'bits', 'wuauserv') {
        try { Start-Service -Name $s -ErrorAction Stop; [void]$log.AppendLine("Dienst $s gestartet") }
        catch { [void]$log.AppendLine("Dienst $s : $($_.Exception.Message)") }
    }
    if ($ok) { Add-Aktion 'Windows Update' $t0 'OK' 'Update-Dienste neu gestartet, Update-Cache und catroot2 zurueckgesetzt (alte Ordner als .bak_ gesichert)' 'Stop-Service; Rename-Item; Start-Service' $log.ToString() }
    else { Add-Aktion 'Windows Update' $t0 'WARNUNG' 'Zuruecksetzen nur teilweise moeglich (Ordner in Benutzung)' 'Stop-Service; Rename-Item; Start-Service' $log.ToString() }
}

# ---------------------------------------------------------------------------
# Netzwerk zuruecksetzen
# ---------------------------------------------------------------------------
if ($auswahl -contains 'netzwerk') {
    $b = Bereich 'netzwerk'
    Schritt 'Netzwerk zurücksetzen (Winsock, TCP/IP, DNS)' $b[0]
    $t0 = Get-Date
    $log = New-Object System.Text.StringBuilder
    $fehler = $false
    foreach ($cmd in @(@('netsh.exe', 'winsock reset'), @('netsh.exe', 'int ip reset'), @('netsh.exe', 'int ipv6 reset'), @('ipconfig.exe', '/flushdns'))) {
        $r = Invoke-Kurz $cmd[0] ($cmd[1] -split ' ')
        [void]$log.AppendLine("> $($cmd[0]) $($cmd[1])  (Code $($r.Code))`n$($r.Ausgabe)`n")
        if ($r.Code -ne 0 -and $cmd[1] -ne 'int ipv6 reset') { $fehler = $true }
    }
    $script:Neustart = $true
    if (-not $fehler) { Add-Aktion 'Netzwerk' $t0 'OK' 'Winsock, TCP/IP und DNS-Cache zurueckgesetzt - wirksam nach Neustart' 'netsh winsock reset; netsh int ip reset; netsh int ipv6 reset; ipconfig /flushdns' $log.ToString() }
    else { Add-Aktion 'Netzwerk' $t0 'WARNUNG' 'Zuruecksetzen teilweise fehlgeschlagen' 'netsh winsock reset; netsh int ip reset; ipconfig /flushdns' $log.ToString() }
}

# ---------------------------------------------------------------------------
# Druckwarteschlange
# ---------------------------------------------------------------------------
if ($auswahl -contains 'druck') {
    $b = Bereich 'druck'
    Schritt 'Druckwarteschlange zurücksetzen' $b[0]
    $t0 = Get-Date
    try {
        Stop-Service -Name Spooler -Force -ErrorAction Stop
        $spool = Join-Path $env:windir 'System32\spool\PRINTERS'
        $n = @(Get-ChildItem $spool -File -ErrorAction SilentlyContinue).Count
        Get-ChildItem $spool -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Start-Service -Name Spooler -ErrorAction Stop
        Add-Aktion 'Druckwarteschlange' $t0 'OK' "Druckdienst neu gestartet, $n haengende Druckauftraege entfernt" 'Stop-Service Spooler; spool\PRINTERS leeren; Start-Service Spooler'
    }
    catch {
        try { Start-Service -Name Spooler -ErrorAction SilentlyContinue } catch {}
        Add-Aktion 'Druckwarteschlange' $t0 'FEHLER' $_.Exception.Message 'Stop-Service/Start-Service Spooler'
    }
}

# ---------------------------------------------------------------------------
# Zeit synchronisieren
# ---------------------------------------------------------------------------
if ($auswahl -contains 'zeit') {
    $b = Bereich 'zeit'
    Schritt 'Uhrzeit synchronisieren' $b[0]
    $t0 = Get-Date
    try { if ((Get-Service w32time).Status -ne 'Running') { Start-Service w32time -ErrorAction Stop; Start-Sleep 2 } } catch {}
    $r = Invoke-Kurz 'w32tm.exe' @('/resync', '/force')
    if ($r.Code -eq 0) { Add-Aktion 'Uhrzeit' $t0 'OK' 'Uhrzeit mit dem Zeitserver synchronisiert' 'w32tm /resync /force' $r.Ausgabe }
    else { Add-Aktion 'Uhrzeit' $t0 'WARNUNG' "Synchronisation fehlgeschlagen (Code $($r.Code), evtl. kein Internet)" 'w32tm /resync /force' $r.Ausgabe }
}

# ---------------------------------------------------------------------------
# Reparaturprotokoll schreiben
# ---------------------------------------------------------------------------
Schritt 'Reparaturprotokoll schreiben' 99
$nFehler = @($Aktionen | Where-Object Ergebnis -eq 'FEHLER').Count
$nWarn   = @($Aktionen | Where-Object Ergebnis -eq 'WARNUNG').Count
$nOk     = @($Aktionen | Where-Object Ergebnis -eq 'OK').Count
$klasse  = if ($nFehler) { 'krit' } elseif ($nWarn) { 'warn' } else { 'ok' }
$zusammenfassung = "$($Aktionen.Count) Aktion(en): $nOk OK, $nWarn Warnung(en), $nFehler Fehler"
if ($script:Neustart) { $zusammenfassung += ' - Neustart erforderlich' }

$json = Join-Path $ordner 'Reparaturprotokoll.json'
[pscustomobject]@{
    Erstellt        = $jetzt.ToString('o')
    Beendet         = (Get-Date).ToString('o')
    Computer        = $env:COMPUTERNAME
    Auswahl         = $auswahl
    Neustart        = $script:Neustart
    Zusammenfassung = $zusammenfassung
    Aktionen        = $Aktionen.ToArray()
} | ConvertTo-Json -Depth 5 | Out-File $json -Encoding UTF8

function HtmlEnc([string]$s) { [System.Net.WebUtility]::HtmlEncode($s) }
$zeilen = foreach ($a in $Aktionen) {
    $kl = @{ OK = 'ok'; INFO = 'info'; WARNUNG = 'warn'; FEHLER = 'krit' }[$a.Ergebnis]
    "<tr><td>$(HtmlEnc $a.Aktion)</td><td>$(HtmlEnc $a.Start)<br>$(HtmlEnc $a.Ende)</td><td class='$kl'>$($a.Ergebnis)</td><td>$(HtmlEnc $a.Details)<br><code>$(HtmlEnc $a.Befehl)</code>" +
    $(if ($a.Ausgabe) { "<details><summary>Ausgabe</summary><pre>$(HtmlEnc $a.Ausgabe)</pre></details>" } else { '' }) + "</td></tr>"
}
$html = @"
<!DOCTYPE html><html lang="de"><head><meta charset="utf-8"><title>Reparaturprotokoll $(HtmlEnc $env:COMPUTERNAME)</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;background:#fff;font-size:13px}
h1{font-size:22px;margin:0 0 4px}.sub{color:#555;margin-bottom:12px}
table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:5px 7px;text-align:left;vertical-align:top}
th{background:#f0f0f0}pre{white-space:pre-wrap;font-size:11px;background:#f5f5f5;padding:6px}code{font-size:11px;color:#555}
.ok{color:#1e7d32;font-weight:bold}.warn{color:#b26a00;font-weight:bold}.krit{color:#c62828;font-weight:bold}.info{color:#1565c0;font-weight:bold}
.box{padding:10px 14px;border-radius:6px;color:#fff;font-weight:bold;margin:10px 0}.box.ok{background:#1e7d32;color:#fff}.box.warn{background:#b26a00;color:#fff}.box.krit{background:#c62828;color:#fff}
</style></head><body>
<h1>Reparaturprotokoll</h1>
<div class="sub">$(HtmlEnc $env:COMPUTERNAME) &middot; Beginn $($jetzt.ToString('dd.MM.yyyy HH:mm:ss')) &middot; Ende $((Get-Date).ToString('dd.MM.yyyy HH:mm:ss')) &middot; Benutzer $(HtmlEnc "$env:USERDOMAIN\$env:USERNAME")</div>
<div class="box $klasse">$(HtmlEnc $zusammenfassung)</div>
<p>Alle Reparaturen wurden ausschliesslich mit Windows-Bordmitteln (DISM, SFC, Repair-Volume, pnputil, netsh, Dienste) durchgefuehrt.</p>
<table><tr><th>Aktion</th><th>Start / Ende</th><th>Ergebnis</th><th>Details</th></tr>
$($zeilen -join "`n")
</table></body></html>
"@
$htmlPfad = Join-Path $ordner 'Reparaturprotokoll.html'
$html | Out-File $htmlPfad -Encoding UTF8

$zeilenHash = @("SHA256-Pruefsummen - erstellt am $((Get-Date).ToString('dd.MM.yyyy HH:mm:ss'))", '')
$zeilenHash += Get-ChildItem $ordner -File | Sort-Object Name | Get-FileHash -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) }
$zeilenHash | Out-File (Join-Path $ordner 'Pruefsummen_SHA256.txt') -Encoding UTF8

if ($Gui) {
    [Console]::Out.WriteLine(("##RR`t{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $klasse, $zusammenfassung, $(if ($script:Neustart) { 1 } else { 0 }), $ordner, $json, $htmlPfad))
    [Console]::Out.Flush()
}
else {
    Write-Host ''
    Write-Host $zusammenfassung -ForegroundColor $(if ($klasse -eq 'ok') { 'Green' } elseif ($klasse -eq 'warn') { 'Yellow' } else { 'Red' })
    Write-Host "Protokoll: $htmlPfad"
}
exit 0
