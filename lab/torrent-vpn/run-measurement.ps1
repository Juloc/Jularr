# Misst den ganzen Weg an einem Anbieter: Tunnel, Lecks, Torrent, Kill-Switch.
#
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/run-measurement.ps1
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/run-measurement.ps1 -Config <datei>
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/run-measurement.ps1 -Key <proton-schluessel>
#
# -Config nimmt die Konfiguration, die aus dem Dashboard des Anbieters heruntergeladen wurde
# (Proton: WireGuard, ein Wert fuer alle Server). Liegt sie schon in vpn\, genuegt der Pfad
# vpn\eingabe.conf. Ohne -Config und ohne -Key wird der Tunnel so benutzt, wie er zuletzt
# vorbereitet wurde. Ein Konto und ein Kennwort des Anbieters werden nirgends gebraucht.
param([string]$Config = "", [string]$Key = "", [string]$KeyFile = "", [int]$Minutes = 3)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot

function Compose { docker compose -f "$root\compose.yaml" -f "$root\compose.vpn.yaml" @args }

Write-Host "1) Die Konfiguration des Anbieters"
if ($Config) {
    & powershell -ExecutionPolicy Bypass -File "$root\prepare-config.ps1" -Config $Config | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "Die Konfiguration wurde nicht angenommen."; exit 1 }
    Write-Host "   die Konfiguration ist eingetragen"
} elseif ($Key -or $KeyFile) {
    & powershell -ExecutionPolicy Bypass -File "$root\prepare-proton.ps1" -Key $Key -KeyFile $KeyFile | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "Der Schluessel fehlt."; exit 1 }
    Write-Host "   Proton: der Schluessel ist eingetragen"
} elseif (Test-Path "$root\vpn\eingabe.conf") {
    & powershell -ExecutionPolicy Bypass -File "$root\prepare-config.ps1" -Config "$root\vpn\eingabe.conf" | Out-Null
    Write-Host "   vpn\eingabe.conf ist eingetragen"
} elseif (Test-Path "$root\vpn\vpn.env") {
    Write-Host "   die eingetragene Konfiguration wird benutzt"
} else {
    Write-Host "   es ist keine Konfiguration eingetragen."
    Write-Host "   Die Datei aus dem Dashboard hier ablegen: $root\vpn\eingabe.conf"
    Write-Host "   und dann: powershell -ExecutionPolicy Bypass -File $root\run-measurement.ps1 -Config $root\vpn\eingabe.conf"
    exit 1
}

Write-Host "2) Der Tunnel startet"
# Immer neu erzeugen: nach dem Kill-Switch vom letzten Lauf haengt der Tunnel im Container
# fest - er meldet sich bereit, hat aber keine Route mehr. Nur ein neuer Container hat sie.
Compose up -d --force-recreate | Out-Null
Start-Sleep -Seconds 40
& powershell -ExecutionPolicy Bypass -File "$root\sync-qbittorrent-password.ps1" | Out-Null

Write-Host "3) Die Messung ($Minutes Minuten)"
if ($Minutes -gt 3) {
    $script = Get-Content "$root\run.sh" -Raw
    $script = $script.Replace("seq 1 18", "seq 1 " + ($Minutes * 6))
    [IO.File]::WriteAllText("$root\run.sh", $script, (New-Object Text.UTF8Encoding($false)))
}
Compose --profile probe run --rm probe 2>&1 | Select-Object -Last 60

Write-Host ""
Write-Host "4) Die Deutung"
Write-Host "   Adresse im Tunnel ungleich der des Hosts  -> der Verkehr laeuft durch den Anbieter"
Write-Host "   DNS antwortet, IPv6 aus                   -> keine Lecks"
Write-Host "   Torrent: Fortschritt und Speed            -> P2P geht"
Write-Host "   Torrent: 0 Byte, aber Tracker meldet     -> der Anbieter sperrt den Verkehr zwischen den Peers"
Write-Host "   Tracker: timed out                        -> der Tracker antwortet durch den Tunnel nicht"
Write-Host "   Nach dem Kill keine Verbindung            -> der Kill-Switch haelt"
