# Die Anbieter im Vergleich

Stand: 2026-10-22, nach den Angaben der Anbieter (geprüft, nicht selbst gemessen).

## Was Privado wirklich gibt (geprüft am 2026-10-22)

| Was | Free | Premium |
| --- | --- | --- |
| Konfigurations-Generator (`.ovpn`/WireGuard) | **nein** – die KB sagt wörtlich: „The generator is only available for Premium customers. Freemium users can use OpenVPN and Wireguard only through the PrivadoVPN app." | ja, im Dashboard unter „Manual Configuration" |
| SOCKS5-Gateway | **nein** – die KB verkauft den Proxy als Premium-Feature | ja, die Zugangsdaten stehen im Dashboard |
| Linux-Programm | **nein** – die Download-Seite verweist für Linux auf die manuellen Anleitungen | dasselbe |
| Daten | 10 GB im Monat | unbegrenzt |
| Verbindungen | 1 gleichzeitig | 10 gleichzeitig |
| Server | 10 (Free) | alle Standorte |

Zwei Wege bleiben, beide ohne Eingriff in den Anbieter:

1. **Die Konfiguration aus dem Ordner der App holen.** Die App baut für die Verbindung
   intern ein OpenVPN- oder WireGuard-Profil; das liegt nach dem Verbinden als Datei auf der
   Platte. `find-privado-config.ps1` sucht danach und `prepare-config.ps1` macht daraus die
   Form, die der Container braucht. Das ist ein Dateikopieren, kein Zugriff auf eine Schnittstelle
   des Anbieters.
2. **Premium kaufen** (derzeit 1,11 €/Monat). Dann gibt es den offiziellen Generator und
   offiziell dokumentierte Anleitungen für qBittorrent, Deluge, uTorrent, Vuze und BiglyBT
   über das SOCKS5-Gateway.

Was nicht geht: die Dashboard-Schnittstelle nachzubauen, um den Generator auch im Gratis-Tarif
zu benutzen. Die Nutzungsbedingungen (Stand 1. Mai 2026) untersagen das ausdrücklich – 8.2 (h)
automatisierte Zugriffe, 8.2 (i) das Umgehen von Nutzungsgrenzen und 6.1 (c) Reverse Engineering.

## Was es schon gibt (geprüft am 2026-10-22)

| Projekt | Was es tut | Stand |
| --- | --- | --- |
| `hongkongkiwi/privado-wireguard-vpn` (21 Sterne, Shell) | holt die WireGuard-Zugangsdaten über die Privado-API mit dem eigenen Konto | das README sagt selbst: nicht mehr nötig, die Weboberfläche kann die Konfiguration inzwischen selbst erzeugen |
| `eagle10162026/privado-vpn-linux` (Rust, AGPL-3.0) | inoffizieller Linux-Client (IKEv2/WireGuard/OpenVPN) mit eigenem Kill-Switch und DNS-Schutz, gebaut durch Interoperabilitäts-Forschung am offiziellen Android-APK | Code fertig, aber laut eigener Angabe noch nicht gegen echte Server getestet |
| `digitalCanine/PrivadoVPN-CLI` | Kommandozeile für Linux über OpenVPN | - |
| `boboysdadda/qbittorrent-privado-vpn` | qBittorrent mit dem SOCKS5-Weg | - |

Der Hinweis im ersten Projekt ist der wichtige: Privado hat auf die API-Nutzung nicht mit
einem Verbot, sondern mit dem Generator in der Weboberfläche reagiert. Im Dashboard unter
**app.privadovpn.com → admin-panel → „Manual Configuration"** lässt sich deshalb prüfen, ob
der Generator für das eigene Konto sichtbar ist. Die Erstellerin des Projekts fragt in Issue #2
ausdrücklich, ob der Abschnitt nur regional vorhanden ist – das ist offen.

**Die Prüfung, die 30 Sekunden braucht:** anmelden, nach unten scrollen, „Manual Configuration"
suchen. Ist der Abschnitt da, gibt es die Konfiguration dort als Datei, und `prepare-config.ps1`
nimmt sie direkt. Ist er nicht da, bleiben die Wege darüber: Premium, der inoffizielle
Linux-Client als Client im Container (dann gehört der Kill-Switch dem Container, nicht dem
Anbieter) oder ein anderer Anbieter mit Konfiguration im Gratis-Tarif.

## Die Anbieter im Vergleich

| Anbieter | Kostenlos | Konfigurationsdatei | Linux-Client | P2P im Gratis-Tarif | Bemerkung |
| --- | --- | --- | --- | --- | --- |
| Privado | 10 GB/Monat | nur Premium | nein, nur manuell | nicht zugesagt | aus Deutschland ist das Portal nicht erreichbar |
| hide.me | unbegrenzt | **ja** | App und manuell (OpenVPN, IKEv2, WireGuard) | im Lab messen | Konto und Anleitungen aus Deutschland erreichbar |
| Windscribe | begrenzt | **ja** | App und CLI | **nein** (im Gratis-Tarif deaktiviert) | der Konto-Host ist aus Deutschland blockiert |
| Proton | unbegrenzt | **ja** | App und CLI | **nein** | Konto aus Deutschland erreichbar |
| beliebiger Anbieter | je nach Anbieter | **ja** | – | je nach Anbieter | der tragfähige Weg für Gluetun |

## Erreichbarkeit aus Deutschland (gemessen am 2026-10-22, Vodafone BW)

Gemessen von diesem Rechner aus (öffentliche IP in Deutschland). TCP-Verbindung und
TLS-Handshake wurden getrennt geprüft, weil ein Block sich je nach Ort unterschiedlich zeigt.

| Anbieter | Website | Konto/Dashboard | Befund |
| --- | --- | --- | --- |
| Privado | ✓ | **✗** | `app.privadovpn.com`: TCP und TLS gehen, aber die HTTP-Antwort kommt nie – 25 s ohne Antwort. Marketingseite und Support-KB antworten. |
| Windscribe | ✓ | **✗** | `my.windscribe.com`: der TLS-Handshake wird abgebrochen. |
| hide.me | ✓ | ✓ | `member.hide.me` antwortet, die Linux-Anleitungen (OpenVPN, IKEv2, WireGuard) sind erreichbar |
| Proton | ✓ | ✓ | `account.protonvpn.com` antwortet |
| Mullvad, IVPN | ✓ | - | Seiten erreichbar, aber kein Gratis-Tarif |

**Folge für Privado:** Das Portal antwortet aus Deutschland nicht – weder die App noch das
Dashboard lassen sich anmelden. Damit ist die Frage nach der Konfiguration für Privado aus
Deutschland praktisch erledigt: Der Generator nützt nichts, wenn das Konto nicht erreichbar ist.
Der Weg für dieses Lab führt über einen Anbieter, dessen Konto erreichbar ist (hide.me, Proton)
oder über eine fremde Leitung.

Ob ein Anbieter Torrents durchlässt, misst das Lab selbst: der Torrent-Client im Tunnel zeigt
es in den Raten und in der Anzahl der Peers.

## Der Weg, den das Lab prüft

Gluetun nimmt eine Konfigurationsdatei eines Anbieters, der eine im Gratis-Tarif herausgibt.
Das ist der Weg, den Jularr später nur als *Konfiguration* braucht: der Anbieter ist austauschbar,
weil Jularr ihn nicht kennt – der Container kennt ihn.

## Die Grenzen

- Ohne VPN-Verbindung darf kein Download weiterlaufen: das ist der Kill-Switch von Gluetun plus
  die Regel, dass die Firewall des Tunnels die ausgehenden Ports des Torrent-Clients sperrt.
- Die kostenlosen Tarife haben oft kein P2P – dort ist der Torrent nur ein Messtest der Mechanik,
  nicht des Durchsatzes. Für gesperrte Quellen, die direkt ausgeliefert werden, reichen die
  10 GB des Gratis-Tarifs lange.
- IPv6 muss im Tunnel mitgenommen oder abgeschaltet sein, sonst läuft ein Teil des Verkehrs
  am VPN vorbei.
