# Die Anbieter im Vergleich

Stand: 2026-10-21, nach den Angaben der Anbieter (nicht selbst gemessen).

| Anbieter | Kostenlos | Konfigurationsdatei | Linux-Client | P2P im Gratis-Tarif | Bemerkung |
| --- | --- | --- | --- | --- | --- |
| Privado | 10 GB/Monat | nein | App (CLI/GUI) | vermutlich ja, prüfen | die App braucht ein Konto; der Weg über den Gateway-Port |
| hide.me | unbegrenzt | nein | App | vermutlich ja, prüfen | dito |
| Windscribe | begrenzt | **ja** (.ovpn) | App und CLI | **nein** (im Gratis-Tarif deaktiviert) | gut für den Konfigurationsweg, nicht für Torrents |
| Proton | unbegrenzt | **ja** (.ovpn) | App und CLI | **nein** | dito |
| beliebiger Anbieter | je nach Anbieter | **ja** | – | je nach Anbieter | der tragfähige Weg für Gluetun |

## Der Weg, den das Lab prüft

Gluetun nimmt eine Konfigurationsdatei eines Anbieters, der eine im Gratis-Tarif herausgibt.
Das ist der Weg, den Jularr später nur als *Konfiguration* braucht: der Anbieter ist austauschbar,
weil Jularr ihn nicht kennt – der Container kennt ihn.

## Die Grenzen

- Ohne VPN-Verbindung darf kein Download weiterlaufen: das ist der Kill-Switch von Gluetun plus
  die Regel, dass die Firewall des Tunnels die ausgehenden Ports des Torrent-Clients sperrt.
- Die kostenlosen Tarife haben kein P2P – dort ist der Torrent nur ein Messtest der Mechanik,
  nicht des Durchsatzes.
- IPv6 muss im Tunnel mitgenommen oder abgeschaltet sein, sonst läuft ein Teil des Verkehrs
  am VPN vorbei.
