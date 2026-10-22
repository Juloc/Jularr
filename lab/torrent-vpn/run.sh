#!/usr/bin/env bash
# The measurements of the lab: the address the world sees, the leaks, the torrent and the
# kill switch. It answers what can be automated in a container and what actually works.
#
# Without a tunnel the probe measures the direct connection. With a tunnel the probe runs
# inside the tunnel's own network space, so every request it makes goes through the tunnel -
# that is what makes the kill switch measurable: when the tunnel is down, the requests fail
# instead of slipping out unencrypted.
set -uo pipefail

say() { printf '\n=== %s\n' "$1"; }
qbit_login() {
    curl -fsS --max-time 15 -c /tmp/cookie -b /tmp/cookie \
        -H "Referer: ${QBITTORRENT_URL}" \
        -d "username=${QBITTORRENT_USER:-admin}&password=${QBITTORRENT_PASSWORD}" \
        "${QBITTORRENT_URL}/api/v2/auth/login" >/dev/null 2>&1
}
qbit_status() { qbit_login; curl -fsS --max-time 15 -b /tmp/cookie -H "Referer: ${QBITTORRENT_URL}" "${QBITTORRENT_URL}/api/v2/torrents/info" 2>/dev/null; }
# The control server of the tunnel only answers with the key.
vpn() { curl -fsS --max-time 12 -H "X-API-Key: ${API_KEY}" "$@" 2>/dev/null; }

# The address of a request, or the reason it did not come.
public_ip() {
    local out
    out=$(curl -fsS --max-time 20 https://api.ipify.org 2>/dev/null) && { printf '%s' "$out"; return 0; }
    printf 'keine Verbindung'
    return 1
}

say "1) Der Zustand des Tunnels"
if [ -n "${VPN_CONTROL_URL}" ]; then
    # The tunnel may still be coming up, and it may be stopped from an earlier run. Wait for the
    # address the world sees: that is only there when the tunnel really stands.
    for i in $(seq 1 24); do
        state=$(vpn "${VPN_CONTROL_URL}/v1/vpn/status" | grep -o '"status":"[^"]*"' | head -1)
        case "$state" in
            *stopped*) vpn -X PUT -H "Content-Type: application/json" -d '{"status":"running"}' "${VPN_CONTROL_URL}/v1/vpn/status" >/dev/null ;;
        esac
        tunnel_ip=$(vpn "${VPN_CONTROL_URL}/v1/publicip/ip" | grep -o '"public_ip":"[^"]*"' | head -1)
        [ -n "$tunnel_ip" ] && break
        sleep 5
    done
    say "   Zustand: ${state:-unbekannt}"
    say "   Adresse im Tunnel: ${tunnel_ip:-keine}"
    say "   (die Adresse ohne Tunnel ist die des Hosts, siehe RESULT.md)"
else
    say "   kein Tunnel im Spiel (Probe ohne VPN-Overlay)"
fi

say "2) Die Adresse, die die Welt sieht"
say "   aus der Probe heraus: $(public_ip)"

say "3) Leck: DNS und IPv6"
dns=$(curl -fsS --max-time 15 'https://cloudflare-dns.com/dns-query?name=example.com&type=A' -H 'accept: application/dns-json' 2>/dev/null | head -c 90)
say "   DNS: ${dns:-keine Antwort}"
v6=$(curl -6 -fsS --max-time 10 https://api6.ipify.org 2>/dev/null)
say "   IPv6: ${v6:-kein IPv6}"

say "4) Der Torrent-Client im Container"
qbit_login
say "   Version: $(curl -fsS --max-time 10 -b /tmp/cookie -H "Referer: ${QBITTORRENT_URL}" "${QBITTORRENT_URL}/api/v2/app/version" 2>/dev/null || echo 'keine Antwort')"

say "5) Der Torrent laeuft"
# A fresh start every time: the old torrent and its file go, so the rate is measured from zero.
curl -fsS --max-time 20 -b /tmp/cookie -H "Referer: ${QBITTORRENT_URL}" -d "hashes=all&deleteFiles=true" "${QBITTORRENT_URL}/api/v2/torrents/delete" >/dev/null 2>&1
curl -fsS --max-time 90 "${TORRENT_URL}" -o /tmp/test.torrent 2>/dev/null
curl -fsS --max-time 30 -b /tmp/cookie -H "Referer: ${QBITTORRENT_URL}" -F "torrents=@/tmp/test.torrent" "${QBITTORRENT_URL}/api/v2/torrents/add" >/dev/null 2>&1
for i in $(seq 1 18); do
    sleep 10
    status=$(qbit_status)
    progress=$(printf '%s' "$status" | grep -o '"progress":[0-9.]*' | head -1)
    dlspeed=$(printf '%s' "$status" | grep -o '"dlspeed":[0-9]*' | head -1)
    peers=$(printf '%s' "$status" | grep -o '"num_complete":[0-9]*' | head -1)
    available=$(printf '%s' "$status" | grep -o '"availability":[0-9.]*' | head -1)
    say "   nach $((i * 10))s: $progress $dlspeed $peers $available"
done
say "   Dateien: $(ls -l "${DOWNLOAD_DIR}" 2>/dev/null | tail -n +2 | head -3)"

say "5b) Der Tracker: erreichbar oder nicht"
hash=$(qbit_status | grep -o '"hash":"[^"]*"' | head -1 | cut -d'"' -f4)
if [ -n "$hash" ]; then
    messages=$(curl -fsS --max-time 15 -b /tmp/cookie -H "Referer: ${QBITTORRENT_URL}" "${QBITTORRENT_URL}/api/v2/torrents/trackers?hash=${hash}" 2>/dev/null |
        grep -o '"msg":"[^"]*"' | grep -v '"msg":""' | sort -u | head -4)
    say "   ${messages:-keine Meldung}"
else
    say "   kein Torrent in der Liste"
fi

say "6) Der Kill-Switch: ohne Tunnel kein Download"
if [ -n "${VPN_CONTROL_URL}" ]; then
    say "   Der Tunnel wird gestoppt (der Container laeuft weiter, die Firewall bleibt zu) ..."
    vpn -X PUT -H "Content-Type: application/json" -d '{"status":"stopped"}' "${VPN_CONTROL_URL}/v1/vpn/status" >/dev/null
    sleep 5
    say "   Zustand danach: $(vpn "${VPN_CONTROL_URL}/v1/vpn/status" | grep -o '"status":"[^"]*"' | head -1)"
    say "   Adresse aus der Probe heraus: $(public_ip)"
    say "   Torrent nach dem Kill: $(qbit_status | head -c 200)"
    say "   Ende der Messung"
else
    say "   kein Tunnel im Spiel, es gibt nichts zu schliessen"
fi
