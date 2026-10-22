# Traegt das temporaere Passwort des Torrent-Clients in die Datei .env des Labs.
#
#   powershell -ExecutionPolicy Bypass -File lab/torrent-vpn/sync-qbittorrent-password.ps1
#
# Der Client vergibt bei jedem frischen Start ein neues Passwort und schreibt es in sein
# Protokoll. Ohne dieses Skript schlaegt die Anmeldung der Messung fehl.
param([string]$Container = "jularr-torrent-vpn-qbittorrent-1")

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$envFile = Join-Path $root ".env"

$password = (docker logs $Container 2>&1 | Select-String -Pattern "temporary password is provided for this session: (\S+)" |
    ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Last 1)
if (-not $password) {
    Write-Host "Im Protokoll des Clients steht kein Passwort. Laeuft er ueberhaupt?"
    exit 1
}

$key = ""
if (Test-Path $envFile) {
    $line = Get-Content $envFile | Where-Object { $_ -match "^VPN_API_KEY=" } | Select-Object -First 1
    if ($line) { $key = $line }
}
if (-not $key) {
    $key = "VPN_API_KEY=" + [Guid]::NewGuid().ToString("N").Substring(0, 24)
}

[IO.File]::WriteAllLines($envFile, @("QBITTORRENT_PASSWORD=$password", $key), (New-Object Text.UTF8Encoding($false)))
Write-Host "Passwort des Torrent-Clients uebernommen."
Write-Host "Der Schluessel des Steuerkanals ist unveraendert."
