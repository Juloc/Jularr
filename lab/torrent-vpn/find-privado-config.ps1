# Findet die VPN-Konfiguration, die die Privado-App auf diesem Rechner bereits erzeugt hat,
# und legt sie in den Lab-Ordner, damit Gluetun sie als Konfiguration verwenden kann.
#
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/find-privado-config.ps1
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/find-privado-config.ps1 -Copy
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/find-privado-config.ps1 -Path <ordner>
#
# Das Skript liest nur Dateien, die bereits auf diesem Rechner liegen. Es meldet keine
# Geheimnisse: Schluessel und Kennwoerter werden weder ausgegeben noch in die Konfiguration
# geschrieben, solange der Anbieter sie nicht selbst in der Datei mitliefert.
param([switch]$Copy, [string]$Path = "")

$ErrorActionPreference = "Continue"
$target = Join-Path $PSScriptRoot "vpn"

# Die Orte, an denen die App ihre Verbindung Profil ablegt.
$roots = @(
    (Join-Path $env:ProgramFiles "Privado VPN"),
    (Join-Path ${env:ProgramFiles(x86)} "Privado VPN"),
    (Join-Path $env:ProgramFiles "PrivadoVPN"),
    (Join-Path $env:LOCALAPPDATA "Privado VPN"),
    (Join-Path $env:LOCALAPPDATA "PrivadoVPN"),
    (Join-Path $env:LOCALAPPDATA "Privado"),
    (Join-Path $env:APPDATA "Privado VPN"),
    (Join-Path $env:APPDATA "PrivadoVPN"),
    (Join-Path $env:APPDATA "Privado"),
    (Join-Path $env:ProgramData "Privado VPN"),
    (Join-Path $env:ProgramData "PrivadoVPN"),
    (Join-Path $env:ProgramData "Privado"),
    (Join-Path $env:ProgramData "OpenVPN\config"),
    (Join-Path $env:LOCALAPPDATA "OpenVPN\config"),
    (Join-Path $env:ProgramFiles "WireGuard\Data\Configurations")
)

# Ein eigener Ordner ersetzt die Liste; damit laesst sich auch ein anderer Anbieter pruefen.
if ($Path) { $roots = @($Path) }

# Merkmale, an denen eine Privado Datei zu erkennen ist.
$markers = "privado", "prvdo", "privadovpn"

function Get-ProfileKind($text) {
    if ($text -match "(?im)^\s*\[Interface\]") { return "wireguard" }
    if ($text -match "(?im)^\s*(remote\s|openvpn\s|<ca>)") { return "openvpn" }
    return "unbekannt"
}

function Get-Server($text) {
    $hit = $text | Select-String -Pattern "(?im)^\s*Endpoint\s*=?\s*(\S+)|(?im)^\s*remote\s+(\S+)" | Select-Object -First 1
    if (-not $hit) { return "" }
    return (($hit.Matches[0].Groups[1].Value + $hit.Matches[0].Groups[2].Value) -replace "=", "").Trim()
}

$profiles = @()
foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Recurse -File -Include *.ovpn, *.conf, *.cfg -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -lt 200000 } |
        ForEach-Object {
            $file = $_
            $text = (Get-Content $file.FullName -TotalCount 60 -ErrorAction SilentlyContinue) -join "`n"
            $inName = @($markers | Where-Object { $file.Name -match [regex]::Escape($_) }).Count -gt 0
            $inText = @($markers | Where-Object { $text -match [regex]::Escape($_) }).Count -gt 0
            $kind = Get-ProfileKind $text
            if ($kind -eq "unbekannt" -and -not $inName -and -not $inText) { return }
            $profiles += [pscustomobject]@{
                Pfad = $file.FullName
                Art = $kind
                Server = Get-Server $text
                Merkmal = if ($inName) { "Name" } elseif ($inText) { "Inhalt" } else { "nur Form" }
                EnthaeltSchluessel = $text -match "(?im)^\s*(PrivateKey|auth-user-pass|<key>)"
            }
        }
}

if ($profiles.Count -eq 0) {
    Write-Host "Keine VPN-Konfiguration gefunden."
    Write-Host ""
    Write-Host "Die App legt sie erst an, wenn sie verbunden ist. Ablauf:"
    Write-Host "  1) Privado-App installieren und mit dem Konto anmelden"
    Write-Host "  2) Verbindung herstellen (OpenVPN oder WireGuard)"
    Write-Host "  3) dieses Skript erneut laufen lassen"
    exit 1
}

Write-Host "Gefundene Konfigurationen:"
$profiles | Format-Table -AutoSize | Out-String | Write-Host

if (-not $Copy) {
    Write-Host "Nichts kopiert. Mit -Copy wird die erste passende Datei nach vpn\ gelegt."
    exit 0
}

$chosen = $profiles | Where-Object { $_.Art -ne "unbekannt" } | Select-Object -First 1
if (-not $chosen) { $chosen = $profiles[0] }
$destination = Join-Path $target "jularr.conf"
Copy-Item $chosen.Pfad $destination -Force
Write-Host "Kopiert: $($chosen.Pfad)"
Write-Host "Nach:    $destination"
Write-Host ""
Write-Host "Der Inhalt gehoert nicht ins Repository. Die Datei ist bereits in .gitignore."
