# Schreibt die Angaben fuer Gluetuns eingebauten ProtonVPN-Anbieter.
#
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-proton.ps1 -Key <schluessel>
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-proton.ps1 -KeyFile <datei>
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-proton.ps1 -Key <schluessel> -Country "Netherlands,Japan"
#
# Der Schluessel steht in Proton unter https://account.proton.me/u/0/vpn/WireGuard - er ist
# ein Wert fuer alle Server. Das Skript ruft nichts bei Proton ab; es schreibt nur die Datei,
# aus der der Container seine Angaben liest.
param(
    [string]$Key = "",
    [string]$KeyFile = "",
    [string]$Country = "Netherlands",
    [switch]$AllServers
)

$ErrorActionPreference = "Stop"
$target = Join-Path $PSScriptRoot "vpn"
if (-not (Test-Path $target)) { New-Item -ItemType Directory -Force -Path $target | Out-Null }

if ($KeyFile) {
    if (-not (Test-Path $KeyFile)) { throw "Die Datei wurde nicht gefunden: $KeyFile" }
    $Key = (Get-Content $KeyFile -Raw).Trim()
}

if (-not $Key) {
    Write-Host "Es fehlt der Schluessel aus Proton."
    Write-Host ""
    Write-Host "1) Kostenloses Proton-Konto anlegen: https://account.protonvpn.com/de/signup?plan=free"
    Write-Host "2) https://account.proton.me/u/0/vpn/WireGuard oeffnen"
    Write-Host "3) die Zeile 'PrivateKey' kopieren (ein Wert fuer alle Server)"
    Write-Host "4) dieses Skript mit -Key <schluessel> erneut laufen lassen"
    exit 1
}

if ($Key -notmatch "^[A-Za-z0-9+/]{43}=$") {
    throw "Der Schluessel sieht nicht aus wie ein WireGuard-Schluessel (43 Zeichen base64 mit = am Ende)."
}

$lines = @(
    "VPN_SERVICE_PROVIDER=protonvpn",
    "VPN_TYPE=wireguard",
    "WIREGUARD_PRIVATE_KEY=$Key",
    "FREE_ONLY=on"
)
if (-not $AllServers) { $lines += "SERVER_COUNTRIES=$Country" }
$path = Join-Path $target "vpn.env"
[IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))

Write-Host "Vorbereitet: ProtonVPN (WireGuard)"
Write-Host "  Angaben:  $path"
Write-Host "  Server:   $(if ($AllServers) { 'alle (ohne FREE_ONLY)' } else { $Country })"
Write-Host ""
Write-Host "Der Schluessel steht in der Datei und gehoert nicht ins Repository."
Write-Host "Start: docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d"
Write-Host "Messen: docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml --profile probe run --rm probe"
