# Jularr clean start

Ein ASP.NET-Host: `Jularr.Frontend` startet Razor Pages und API.
`Jularr.Backend` ist eine referenzierte .NET-Klassenbibliothek mit Services, Logic,
Data und PostgreSQL-Zugriff. Kein alter Web-Bereich, kein EF.

`Jularr.Integrations` bündelt externe Kommunikation als Klassenbibliothek.
`Logic/Plex` enthält den Plex-Client, `Data/Plex` seine Protokollantworten
und `Data/Common` gemeinsame Integrationsfehler. Backend-Services
entscheiden weiterhin über Anmeldung, Bibliotheksfreigabe und Registrierung.
Die bestehenden Host-Registrierungen bleiben unverändert. SMB-Freigaben
erreicht Jularr selbst oder Docker bindet sie als Volume ein, NFS immer als Volume;
dazu Synology API und per Docker Compose freigegebene Pfade. Konfiguration erfolgt über Services und Frontend.

Regeln: [AGENTS.md](AGENTS.md), [CONVENTIONS.md](docs/CONVENTIONS.md).
Navigation: [CODEMAP.md](docs/CODEMAP.md).
Nächste fachliche Schritte: [Backend-Slices](docs/BACKEND_NEXT.md) (Plan, keine Implementierung).
Instanz-Einrichtung: [vier Schritte, Presets und Owner](docs/INSTANCE_SETUP.md).
Lokale Vorschau auf D: [Testserver und Aktualisierung](docs/LOCAL_PREVIEW.md).

## Reader und Vorlesen

Der Reader nutzt standardmäßig Gerätestimmen. Optionale neuronale Stimmen laufen
lokal im Browser: Der Owner aktiviert ein Modell in der Modulverwaltung; auf dem
Gerät wird es ausdrücklich installiert, die Stimmwahl im Profil gespeichert.
Keine automatische Modellinstallation und keine serverseitige Sprachsynthese.
[Verhalten, Prüfungen und offene Anschlüsse](docs/SPEECH.md),
[Lizenzumfang des Browser-Clients](src/Jularr.Frontend/BROWSER-LICENSE.md).

### Optionale TranslateGemma-Übersetzung

Bergamot bleibt die kostenlose Browser-Standardübersetzung. Ein Owner kann zusätzlich
TranslateGemma in einem **separat bereitgestellten, privaten Ollama-Dienst** verwenden.
Dessen Modellgewichte und Modell-Laufzeit sind **nicht** Teil des Jularr-Images.
In `.env` kann `JULARR_OLLAMA_ENDPOINT=http://ollama:11434` gesetzt werden, wenn
der Dienst unter diesem Namen aus dem Jularr-Container erreichbar ist. Der
Ollama-Port darf nicht öffentlich freigegeben sein. Nach Neustart des Jularr-Dienstes:
`/Admin/Instance/Translation` öffnen, Speicherbedarf des persistenten Ollama-Volumes
prüfen, `JULARR_OLLAMA_VERIFIED_FREE_BYTES` mit den dort wirklich freien Bytes konfigurieren
(Nullwert sperrt jeden Download; mindestens 2 GB Reserve) und Jularr danach neu
starten. Download in der Modellansicht ausdrücklich auslösen; der Vorgang läuft
im begrenzten Host-Worker weiter und der Status ist durch Aktualisieren sichtbar.
Nach einer Jularr-Unterbrechung kann ein angehaltener Pull erneut gestartet werden;
dies ist noch keine dauerhafte Job-Historie. Anschließend den kurzen Benchmark ausführen.

Zusätzliche lokale Übersetzungen werden im Reader blockweise separat gespeichert;
Bergamot-Übersetzungen werden nicht überschrieben. Generierung über den Server ist
vorerst nur für Owner freigegeben; ein echter Modellserver und die Hardwarequalität
müssen vor produktiver Verwendung geprüft werden. **Der Benchmark ist nur ein
Geschwindigkeits-/Smoke-Test**, keine Prüfung literarischer Genauigkeit. Automatische
Runtime-Installation, dauerhafte Downloadfortschrittsanzeige und erweiterte
Glossar-/QA-Regeln bleiben unter [#881](https://github.com/Juloc/Jularr/issues/881)
und [#985](https://github.com/Juloc/Jularr/issues/985) geplant.

## Docker Compose

[compose.yaml](compose.yaml) braucht nur Image, Port und Volumes; `.env.example` nach `.env`
kopieren, Release und Datenbankpasswort setzen, `docker compose up -d`, dann `http://<host>:8097`
öffnen und den Owner anlegen. Jede Funktion arbeitet ohne weitere Einstellungen: Das
[Image](Dockerfile) bringt Jellyfins FFmpeg (Hardware-Transcoding für Intel, AMD und NVIDIA), das statische fpcalc von Chromaprint, 7-Zip, par2 und den PostgreSQL-18-Client mit
(auf dem `PATH` gefunden; ein gesetzter Pfad gewinnt, ein leerer schaltet das Programm ab), aller
Zustand liegt unter `DataPath` (`/data` im Image: `keys`, `backups`, `trickplay`, `translation`, `ocr`),
Bibliotheken unter `/media` (`Storage:LocalPaths`). Eine leere Datenbank erhält beim ersten Start
alle frischen Baselines in einer Transaktion; eine bestehende wird nie verändert oder migriert.

### Browser für Quellen mit Browserprüfung

**Standardmäßig deaktiviert.** Das optionale Modul **Browser** lässt sich im Adminbereich
unter `/Admin/Instance/Modules` einschalten und wieder ausschalten; es hängt von
**Acquisition** ab. Jularr benötigt dafür keinen zweiten Container, keine Root-Befehle
aus dem Webprozess und keinen öffentlich erreichbaren Debugging-Port.

Beim Einschalten lädt Jularr eine fest vorgegebene, offizielle Chrome-for-Testing-Version
für Linux x64 oder arm64 in `DataPath/browser/managed`. Download und Entpacken haben
Größen- und Pfadgrenzen, eine temporäre Installation wird vor der Aktivierung geprüft.
Der Hintergrunddienst aktiviert Chrome nach der Installation automatisch. Ein Neustart
oder eine Container-Neuerstellung behält den Browser unter dem persistenten `/data`.
Der Installationsfortschritt und Fehler erscheinen unter `/Admin/Browser`; die
Modulverwaltung prüft den Zustand ungefähr alle 30 Sekunden.

Beim Ausschalten wird die Browserbenutzung unterbunden und der eigene Prozess gestoppt.
Jularr entfernt nur seine verwalteten Browserdateien, temporären Dateien, Cookies und
sein Browserprofil; Bibliotheken, NAS-Mounts und andere Downloadordner bleiben erhalten.
Ein vorhandener manuell konfigurierter Browser über `Browser:Path` oder
`Browser:RemoteAddress` bleibt als Konfigurationsoption erhalten.

**Sicherheit:** Die normale HTTPS-Adressprüfung ist keine vollständige Browser-
Netzwerkisolierung. Für nicht vertrauenswürdige Seiten muss die ausgehende
Verbindungsbegrenzung gegen interne IPs auch für Redirects, Skripte, Frames und
nachgeladene Ressourcen noch in einer isolierten Debian-Docker-Umgebung nachgewiesen
werden ([#1483](https://github.com/Juloc/Jularr/issues/1483)). Der optionale Browser
darf vor diesem Test nicht als sicherer Produktivpfad freigegeben werden. Für Prüfungen,
die eine Person lösen muss, zeigt Jularr weiterhin den manuellen Link; es gibt keinen
CAPTCHA-Bypass.

