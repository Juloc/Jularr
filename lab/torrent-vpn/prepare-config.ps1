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

# WireGuard braucht genau einen Peer. Das Dashboard des Anbieters liefert oft alle Server in
# einer Datei; dann wird der erste genommen und der Weg zu ihm aufgeschrieben.
if ($isWireGuard) {
    $blocks = [regex]::Matches($text, "(?im)^\s*\[Peer\].*?(?=^\s*\[|\z)", "Singleline")
    if ($blocks.Count -gt 1) {
        $chosen = $blocks[0].Value
        $endpoint = ""
        if ($chosen -match "(?im)^\s*Endpoint\s*=?\s*(\S+)") { $endpoint = $Matches[1] }
        $body = ($chosen -replace "(?im)^\s*\[Peer\]\s*", "").Trim()
        $text = ($text -replace "(?ims)^\s*\[Peer\].*?(?=^\s*\[|\z)", "")
        $text = $text.TrimEnd() + "`n`n[Peer]`n" + $body + "`n"
        Write-Host "Die Datei hatte $($blocks.Count) Server; genommen wurde der erste ($endpoint)."
    }
    foreach ($needed in @("PrivateKey", "PublicKey", "Endpoint")) {
        if ($text -notmatch "(?im)^\s*$needed\s+\S+") { throw "Die WireGuard-Konfiguration hat kein $needed." }
    }
    # Gluetun verlangt fuer den Endpunkt eine Adresse, keinen Namen.
    if ($text -match "(?im)^\s*Endpoint\s*=?\s*([a-z0-9\.\-]+):(\d+)") {
        $server = $Matches[1]
        $port = $Matches[2]
        if ($server -notmatch "^\d+\.\d+\.\d+\.\d+$") {
            try {
                $address = ([System.Net.Dns]::GetHostAddresses($server) | Where-Object { $_.AddressFamily -eq "InterNetwork" } | Select-Object -First 1)
            } catch {
                throw "Die Adresse des Servers ($server) liess sich nicht aufloesen: $($_.Exception.Message)"
            }
            if (-not $address) { throw "Die Adresse des Servers ($server) liess sich nicht aufloesen." }
            $text = $text -replace "(?im)^(\s*Endpoint\s*=?\s*)$([regex]::Escape($server)):(\d+)", "`$1$address`:$3"
            Write-Host "Der Endpunkt $server wurde zu $address aufgeloest (Gluetun will eine Adresse)."
        }
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

# The name server of the provider stands in the configuration; the container needs it, because
# the address range of the server changes with the server.
$dns = ""
if ($isWireGuard -and $text -match "(?im)^\s*DNS\s*=?\s*(\d+\.\d+\.\d+\.\d+)") { $dns = $Matches[1] }

$type = "openvpn"
if ($isWireGuard) { $type = "wireguard" }
$envLines = @(
    "VPN_SERVICE_PROVIDER=custom",
    "VPN_TYPE=$type",
    "VPN_CUSTOM_CONFIG_PATH=/vpn/jularr.conf"
)
if ($dns) { $envLines += "VPN_DNS=$dns`:53" }
if ($user) { $envLines += "VPN_USER=$user" }
if ($password) { $envLines += "VPN_PASSWORD=$password" }
$envPath = Join-Path $target "vpn.env"
[IO.File]::WriteAllLines($envPath, $envLines, (New-Object Text.UTF8Encoding($false)))

Write-Host "Vorbereitet: $type"
if ($dns) { Write-Host "  Nameserver des Anbieters: $dns" }
Write-Host "  Konfiguration: $configPath"
Write-Host "  Angaben:       $envPath"
Write-Host ""
if ($user) { Write-Host "Zugangsdaten wurden uebernommen (nicht ausgegeben)." }
Write-Host "Start: docker compose -f lab/torrent-vpn/compose.yaml -f lab/torrent-vpn/compose.vpn.yaml up -d"
