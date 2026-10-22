# Macht aus einer Anbieter-Konfiguration die Form, die der Container braucht.
#
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-config.ps1 -Config <datei>
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/prepare-config.ps1 -Config <datei> -Credentials <datei>
#
# Die Konfiguration selbst kommt von Hand aus dem Dashboard des Anbieters (oder aus dem
# Ordner der App, siehe find-privado-config.ps1). Dieses Skript ruft keine API des Anbieters
# auf und stellt keine Zugangsdaten selbst zusammen.
#
# Es schreibt nach vpn\:
#   jularr.conf   die Konfiguration, wie der Tunnel sie braucht
#   vpn.env       die Angaben fuer den Container (Typ, Pfad, Zugangsdaten)
#
# Beides gehoert nicht ins Repository und steht in .gitignore.
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [string]$Credentials = ""
)

$ErrorActionPreference = "Stop"
$target = Join-Path $PSScriptRoot "vpn"
if (-not (Test-Path $target)) { New-Item -ItemType Directory -Force -Path $target | Out-Null }
if (-not (Test-Path $Config)) { throw "Die Konfiguration wurde nicht gefunden: $Config" }

$text = (Get-Content $Config -Raw) -replace "`r`n", "`n"
$isWireGuard = $text -match "(?im)^\s*\[Interface\]"
$isOpenVpn = $text -match "(?im)^\s*(remote\s|openvpn\s|<ca>)"

if (-not $isWireGuard -and -not $isOpenVpn) {
    throw "Die Datei ist weder eine WireGuard- noch eine OpenVPN-Konfiguration."
}

# WireGuard braucht genau einen Peer; mehrere werden nicht unterstuetzt.
if ($isWireGuard) {
    $peers = ([regex]::Matches($text, "(?im)^\s*\[Peer\]")).Count
    if ($peers -ne 1) { throw "Die WireGuard-Konfiguration hat $peers Peer; erwartet wird genau einer." }
    foreach ($needed in @("PrivateKey", "PublicKey", "Endpoint")) {
        if ($text -notmatch "(?im)^\s*$needed\s+\S+") { throw "Die WireGuard-Konfiguration hat kein $needed." }
    }
}

# OpenVPN mit Zugangsdaten braucht die Datei dazu, sonst kann der Tunnel nicht starten.
$user = ""
$password = ""
if ($Credentials) {
    if (-not (Test-Path $Credentials)) { throw "Die Zugangsdaten wurden nicht gefunden: $Credentials" }
    $lines = @(Get-Content $Credentials | Where-Object { $_.Trim() -ne "" })
    if ($lines.Count -lt 2) { throw "Die Zugangsdaten brauchen zwei Zeilen: Benutzer und Kennwort." }
    $user = $lines[0].Trim()
    $password = $lines[1].Trim()
    if ($isOpenVpn -and $text -notmatch "(?im)^\s*auth-user-pass") {
        Write-Host "Hinweis: Die OpenVPN-Konfiguration verlangt keine Zugangsdaten; die werden nicht gebraucht."
        $user = ""
        $password = ""
    }
}

$configPath = Join-Path $target "jularr.conf"
[IO.File]::WriteAllText($configPath, $text, (New-Object Text.UTF8Encoding($false)))

$type = "openvpn"
if ($isWireGuard) { $type = "wireguard" }
$envLines = @(
    "VPN_SERVICE_PROVIDER=custom",
    "VPN_TYPE=$type",
    "VPN_CUSTOM_CONFIG_PATH=/vpn/jularr.conf"
)
if ($user) { $envLines += "VPN_USER=$user" }
if ($password) { $envLines += "VPN_PASSWORD=$password" }
$envPath = Join-Path $target "vpn.env"
[IO.File]::WriteAllLines($envPath, $envLines, (New-Object Text.UTF8Encoding($false)))

Write-Host "Vorbereitet: $type"
Write-Host "  Konfiguration: $configPath"
Write-Host "  Angaben:       $envPath"
Write-Host ""
if ($user) { Write-Host "Zugangsdaten wurden uebernommen (nicht ausgegeben)." }
Write-Host "Start: docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d"
