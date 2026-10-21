#!/usr/bin/env bash
# The measurements of the lab: the address the world sees, the leaks, the torrent and the
# kill switch. It answers what can be automated in a container and what actually works.
set -uo pipefail

say() { printf '\n=== %s\n' "$1"; }
public_ip() { curl -fsS --max-time 20 https://api.ipify.org 2>/dev/null || echo "unbekannt"; }
qbit_login() {
    curl -fsS --max-time 15 -c /tmp/cookie -b /tmp/cookie \
        -H "Referer: ${QBITTORRENT_URL}" \
        -d "username=${QBITTORRENT_USER:-admin}&password=${QBITTORRENT_PASSWORD}" \
        "${QBITTORRENT_URL}/api/v2/auth/login" >/dev/null 2>&1
}
qbit_status() { qbit_login; curl -fsS --max-time 15 -b /tmp/cookie "${QBITTORRENT_URL}/api/v2/torrents/info" 2>/dev/null; }

say "1) Die Adresse, die die Welt sieht"
say "   ausserhalb des Tunnels: $(public_ip)"
say "   im Tunnel:              $(curl -fsS --max-time 20 -x http://vpn:8888 https://api.ipify.org 2>/dev/null || echo 'kein Tunnel')"

say "2) Leck: DNS und IPv6"
say "   DNS ausserhalb: $(curl -fsS --max-time 15 'https://cloudflare-dns.com/dns-query?name=example.com&type=A' -H 'accept: application/dns-json' 2>/dev/null | head -c 90 || echo 'keine')"
say "   IPv6 ausserhalb: $(curl -6 -fsS --max-time 10 https://api6.ipify.org 2>/dev/null || echo 'kein IPv6')"
say "   IPv6 im Tunnel:   $(curl -6 -fsS --max-time 10 -x http://vpn:8888 https://api6.ipify.org 2>/dev/null || echo 'kein IPv6 im Tunnel')"

say "3) Der Torrent-Client im Container"
qbit_login
say "   Version: $(curl -fsS --max-time 10 -b /tmp/cookie "${QBITTORRENT_URL}/api/v2/app/version" 2>/dev/null || echo 'keine Antwort')"

say "4) Der Torrent laeuft"
curl -fsS --max-time 90 "${TORRENT_URL}" -o /tmp/test.torrent
curl -fsS --max-time 30 -b /tmp/cookie -F "torrents=@/tmp/test.torrent" "${QBITTORRENT_URL}/api/v2/torrents/add" >/dev/null 2>&1
for i in 1 2 3 4 5 6; do
    sleep 10
    status=$(qbit_status)
    progress=$(printf '%s' "$status" | grep -o '"progress":[0-9.]*' | head -1)
    dlspeed=$(printf '%s' "$status" | grep -o '"dlspeed":[0-9]*' | head -1)
    say "   nach $((i * 10))s: $progress $dlspeed"
done
say "   Dateien: $(ls -l "${DOWNLOAD_DIR}" 2>/dev/null | tail -n +2 | head -3)"

say "5) Der Kill-Switch: ohne Tunnel kein Download"
say "   Tunnel und Firewall werden geschlossen ..."
curl -fsS --max-time 10 -X POST "${VPN_CONTROL_URL}/v1/openvpn/down" >/dev/null 2>&1
curl -fsS --max-time 10 -X POST "${VPN_CONTROL_URL}/v1/killswitch" >/dev/null 2>&1
sleep 5
say "   Adresse ohne Tunnel: $(public_ip)"
say "   Torrent nach dem Kill: $(qbit_status | head -c 240)"
say "   Ende der Messung"
