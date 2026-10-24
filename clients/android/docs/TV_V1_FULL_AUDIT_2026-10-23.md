# Android TV V1 – vollständiges Architektur-, Seiten- und Legacy-Audit

## Umsetzung aus diesem Audit (Integrations-PR #1540)

**Codeänderungen umgesetzt:** `TvV1ClientFlow.videoPage` und `watchlistPage` holen Anime mit `mediaSubtypeIds=1`; Watchlist ist video-only und sortiert mit tatsächlicher Backend-Reihenfolge (nur `RecentlyAdded=1`, `Title=2`, die der V1-Backend-Enum sicher unterstützt). Die alten UI-Optionen „Älteste“ und „Titel Z–A“ werden im aktiven V1-Sort-Dialog nicht länger fälschlich angeboten. Paging-Anfragen berücksichtigen Sortierwechsel; die Suche und Favoriten können auch aus leeren clientseitigen Filterseiten heraus weitere Serverdaten laden. `TvV1PersonScreen`/CatalogHost laden Person-Works nun auf Anforderung statt 20 Seiten im Voraus.

**Legacy-Pfad entfernt:** `TvAppHost`, `TvAppController`, `TvClientFlow` und 12 reine Legacy-UI/Playback-Helper-Dateien sowie die zugehörigen Legacy-Tests sind aus dem TV-Modul entfernt (24 Dateien im ersten Cleanup-Commit plus `TvNavigationTest`); `TvNavigation.kt` enthält nur noch die im aktiven Sidebar-Code benötigten Ziele, `TvScreens.kt` nur noch das aktive `TvSetupScreen`. Unverzichtbare geteilte Compose-Screens und die V1-Kontoslots in `TvSessionStore` bleiben bewusst erhalten. `clients/android/README.md` ist aktualisiert.

**Weitere Nacharbeit (24.10.):** Android-JVM-Build, Android-Lint sowie Backend/Authorship waren auf `c077f364` grün. Der letzte alte `TvNavigation`-Test wurde auf aktive V1-Fokus-Keys angepasst. Film-/Edition-Assets werden nicht mehr mit 20×100 Anfragen vorab geladen; die Auswahl lädt Seiten bei Bedarf, besitzt eine virtuelle scrollbare D-Pad-Liste und zeigt Fehler/Ladezustände. Die alten Cookie-Sessions und zugehörigen APIs wurden aus `TvSessionStore` entfernt; der Migrations-Start entsorgt nur unbrauchbare Cookie-Daten und erhält verschlüsselte V1-Bearer-Konten und zuvor gepaarte V1-Tokens. Die neue Android-CI nach diesen Änderungen ist gesondert zu prüfen. Die `main`-Änderungen seit Branch-Start betreffen im geprüften Dateivergleich keine überschneidenden Pfade; ein späteres Update auf den aktuellen Stand ist trotzdem erforderlich.

**Noch offen:** Phone-Companion #1508 ist kein bestehender V1-Contract; ohne vollständigen Backend-/Phone-/TV-Workflow keine funktionierende Fernsteuerung. TV-Request-Aktionen, echte automatische LAN-Discovery (kein entsprechender aktueller Backend-Responder gefunden), Release-Signing/automatischer Update-Hinweis, echtes Media3-/NAS-/Keystore-/D-Pad-E2E sind nicht erledigt. Der folgende Bericht bleibt als Ausgangsbefund erhalten, auch wo eine genannte Lücke inzwischen korrigiert wurde. Keine Hardware-Vollparität behaupten.

**Stand:** 2026-10-23 · **Integrations-PR:** #1540 (`feat/android-tv-v1-main-integration`) · **Quelle:** #1468 bei `7a0d17e3b8aae0a0615fd586b5688f8d44461e41` · **Backend-Vergleich:** `main` vom 23.10.2026.

**Prüfart:** Statischer Review der Android-TV-Quellen, aktiven Entrypoints, Compose-Screens, V1-Flows, der Backend-Library-/Collection-/Pairing-Verträge und bisheriger Migration-/E2E-Dokumente. Android-Gradle-Tests/CI sind **kein** tatsächlicher TV-/NAS-/Media3-Gerätetest. Die vorhandene 18-Fälle-Geräteanleitung bleibt verbindlich: `TV_V1_PARITY_E2E_TESTPLAN.md`.

## 1. Echte Laufzeitarchitektur und Grenzen

`AndroidManifest.xml` startet **nur** `TvMainActivity`. Sie verwendet
`TvV1EntryHost` → `TvV1CatalogHost` → `TvV1PlaybackHost`, mit
`TvV1ClientFlow`, `TvV1AppController`/`TvV1Navigation`,
`HttpTvCatalogV1Client`, `HttpTvV1ProfileClient`, `HttpTvPlaybackV1Client`,
`HttpTvV1PlaybackExtrasClient` und dem leider etwas irreführend benannten
`HttpTvV1LegacyFeaturesClient` (**tatsächlich neuer /api/v1-Adapter für Playback-Assets/Statistik**, kein alter Cookie-Endpunkt).

Die zweite Architektur `TvAppHost` → `TvAppController` → `TvClientFlow` wird **nicht** vom TV-Main-Activity-Einstieg gestartet. Sie verwendet `JularrClientApi`, Cookie-Logins, `/api/client/v1`, frühere Playback- und Companion-Schnittstellen. Weder grüne JVM-Tests dieser Altklassen noch deren Bildschirm-Code beweisen, dass die Funktionen in der V1-App zugänglich sind.

## 2. Seiteninventar (aus tatsächlich aktivem Einstieg)

| Route / Dialog | Aktiver Code | Tatsächlich vorgesehen/verdrahtet | Wesentliche Einschränkung |
| --- | --- | --- | --- |
| Server-Einrichtung | `TvV1EntryHost` → `TvSetupScreen` | Manuelle URL, Serverwechsel, Verbindungsfehler | Frühere LAN-Server-Autodiscovery wird im V1-Aufruf **nicht** mit `discovered`/`discovering` gefüttert |
| Geräte-Pairing | `TvV1EntryHost` | 6-stelliger Code, 5-s-Polling, Ablauf/Neustart, Token-Restore | Geräte-E2E/Prozess-Neustart ausstehend |
| Kontoauswahl | `TvV1EntryHost` | Neue verschlüsselte Kontoslots auswählen oder weiteres Konto koppeln | Verwendet neues einfaches `TvCenteredPanel`, **nicht** `TvAccountSelectScreen` |
| Profilwechsel/PIN | `TvV1ProfilesScreen` | Profile laden, PIN-Eingabe, Token-/Capability-Neustart | Keystore-/Profil-Race auf Gerät prüfen |
| Home | `TvHomeScreen` | Hero-Rotation 9 s (bis 8), Fortsetzen, kürzlich hinzugefügt, Filme, Serien, Video-Rankings (Trending/Top/New/Upcoming), Filter und Mehr-Laden | Anime-Filterdefekt unten; Provider-/D-Pad-Tests fehlen |
| Bibliothekssuche | `TvSearchScreen` | Serverbasierte Suche, Film/Serien/Anime-Filter, Paging, Discover-Einstieg | Anime-Untertyp fehlt in V1-Abfrage |
| Discover | `TvV1DiscoverScreen` | Film-/Seriensuche, lokale oder externe TMDB-Details; Anime-Katalog-Einträge von Home | Kein echter TV-Request-Flow für provider-only Ergebnisse; Listen/Detail-UX einfacher als Web |
| Favoriten | `TvWatchlistScreen` | Watchlist, Sortierung, Medienfilter, 100er-Paging, Collection-Chips, Smart Collections lesend | Server liefert kein Datum an UI; „Neueste/Älteste“ nicht echt; Books-Filter trotz Video-TV |
| Werkdetails | `TvAnimeScreen` | Film/Serie/Anime, Staffeln/Folgen, Favoriten ändern, Wiedergabe und Credits | Keine echte Nicht-Video-Detail-/Reader-Seite |
| Episodendetails | `TvV1EpisodeDetailScreen` | WorkUnit-spezifische Infos und Play/Back | Nicht spielbare Assets liefern Pending/Error |
| Cast/Credits | `TvV1CreditsScreen` | Mitwirkende mit PersonId, WorkId | Nur Videokatalog-Pfad |
| Person/Filmografie | `TvV1PersonScreen` | Person und anklickbare Werke | Bis 20×100 Person-Works werden vollständig vorab geladen – kein on-demand Paging |
| Filmversionen | `TvCenteredPanel` in `TvV1CatalogHost` | Verfügbare MediaAsset-Versionen mit Auflösung/Sprache/Größe auswählen | Funktional, aber keine ausgearbeitete Editions-UX |
| Player Pending | `TvV1ApiMessage` | Nicht vorhandenes MediaAsset erklärt | Kein Ersatz-Asset erfunden |
| Player | `TvV1PlaybackHost` → `TvPlayerScreen` | Direct/HLS, Media3, Resume, Progress, Skip/Marker, Trickplay, Kapitel, Track-Wahl, Speed, Vorher/Nächste, Autoplay | Noch kein physischer Realmedien-E2E, PGS/Remote-Sub nicht verfügbar |
| Einstellungen | `TvV1SettingsHost` → `TvProfileScreen` | Autoplay, Audio-/Untertitelpräferenz, Speed, Server/Profil/Konto, Logout, manuelle APK-Updates | Ausstehende Release-Installation/Signierung; kein automatisches V1-Updateprompt nachgewiesen |
| Nutzungsstatistik | `TvV1StatisticsScreen` | Aggregat für 30/90/alle Tage, meistgesehene Werke | **Nicht** chronologischer Verlauf (#1504) |
| Aktive Sitzungen | `TvV1SessionsScreen` | Sessionliste, widerrufbare fremde Sessions mit Bestätigung | Permissions und Token-Races auf Gerät prüfen |
| Fehler/NAS | `TvV1PlaybackHost`-Fehlerpanel + `retryWhileStorageWaking` | `storage_waking` gebunden retry; unavailable erklärt | **Nicht** das alte `TvStorageRecoveryScreen`; kein Profil-NAS-Manual-Wake |

Die Sidebar bietet nur **Home, Favoriten, Einstellungen, Profil**; **Suche** liegt im Home und **Discover** über Suche/Hero. Das ist ein navigations-/D-Pad-UX-Thema, kein fehlender API-Endpunkt.

## 3. Legacy-Code: behalten, entflechten, später entfernen

**A. Inaktive komplette alte Laufzeit (Kandidaten zur Entfernung nach Paritätstest):**
- `TvAppHost.kt` (~1.158 Zeilen), `TvAppController.kt` (~661), `TvClientFlow.kt` (~271); keine Verdrahtung vom Manifest-Entrypoint.
- `TvCompanionRuntime.kt`, `TvCompanionPairingOverlay.kt`, `TvCueTimeline.kt`, `TvStorageRecoveryScreen.kt`, `TvStorageRecoveryPolicy.kt`, `TvSessionCookieStore.kt`: vom aktiven V1-Host nicht verwendet, sondern im Wesentlichen durch die alte Architektur/alte Tests gehalten.
- `TvDeviceCodecSupport.kt`, `TvMediaTrackSelector.kt`, `TvPlaybackPlanner.kt`, `TvProgressPolicy.kt`: alte Playback-Helfer; V1 verwendet `TvV1PlaybackCapabilities`, V1-Playback-Session und direkte V1-Progress-Calls.
- `TvActivityScreen.kt`: keine Route in `TvV1CatalogPage`; auch keine aktive V1-Verwendung; noch dazu fehlt der Chronologie-V1-Vertrag.
- `TvAccountSelectScreen.kt`: alter Account-Chooser; aktiver V1-Chooser sitzt in `TvV1EntryHost`.
- `TvLoginScreen`, `TvEpisodeScreen` in `TvScreens.kt`: alte Login-/Episode-Flächen; V1 verwendet nur das Setup aus dieser Datei und separate V1-Episodendetails.
- `TvUpdatePromptOverlay` (in `TvUpdateScreen.kt`): altes Startup-Prompt nur über `TvAppHost`, während `TvUpdateSection` in V1 genutzt wird.
- `TvV1AuthenticatedPendingCatalog` in `TvV1EntryHost`: tote Übergangsansicht, da `TvMainActivity` tatsächlich den V1-Catalog-Callback liefert.

**B. Nicht pauschal löschen: tatsächlich geteilter UI-/Modellcode:**
- `TvHomeScreen`, `TvSearchScreen`, `TvWatchlistScreen`, `TvAnimeScreen`, `TvPlayerScreen`, `TvProfileScreen`, `TvUpdateSection`, `TvSidebar`, `TvSharedComponents`, `TvPlayerChrome`, `TvFocusMemory`, `TvPlayerInteraction`, `TvPlayerDesign` und `TvContentFilter` werden von V1 weiterverwendet.
- `TvNavigation.kt` enthält **sowohl** alte Navigation **als auch** die immer noch von `TvSidebar` benutzten `TvRoute`-Typen. Nicht löschen, ehe `TvRoute` umgezogen ist.
- `TvScreens.kt` enthält **aktives** `TvSetupScreen` neben altem Login und alter Episode: vor Entfernen aufteilen.
- `TvSessionStore.kt` besitzt **aktive** V1-Kontoslots, Keystore-/Bearer-Persistenz und Migrationslogik neben überholten Cookie-Sessions. Nicht komplett löschen; Alt-Sessions erst mit Migration/Upgrade-Test ausgliedern.
- `core-api`, `core-model`, `core-session`, `core-player` sind mit der Phone-App geteilt. Legacy-API-Klassen können auch für Phone noch nötig sein; **nicht** ohne app-mobile-Abhängigkeitsprüfung entfernen.
- `HttpTvV1LegacyFeaturesClient` ist trotz Namen eine **aktive, echte V1-API** für Playback-Nachbarn und Statistiken.

## 4. Konkrete Fehler und technische Schulden

### P0 / funktionale Korrektheit

**TV-ANIME-FILTER – bestätigt aus Code + aktuellem main-Backend**
`TvV1ClientFlow.videoPage()` sendet `TvCatalogV1Filters(mediaTypeIds = listOf(1, 2))` **ohne** `mediaSubtypeIds`. `ReadLibraryV1` filtert Medientypen ausdrücklich nur bei `MediaSubtypeId IS NULL`; benannte Subtypen wie Anime (Subtype 1) werden ohne separate `mediaSubtypeIds=1` ausgelassen. Das trifft Home, Suche und deren Pagination; anschließend kann der Anime-Filter leer erscheinen, obwohl Anime existieren. Der Collections-Adapter hat die korrekte Subtype-ID bereits explizit. **Fix/Regression:** Bibliothekssuche und -paging inklusive der gewünschten Video-Subtypen, mindestens Anime 1, serverseitig abfragen; echte Titel aus Anime-Serie und Anime-Film in allen betroffenen Listen verifizieren.

### P1 / UI-Korrektheit, Funktionsqualität

1. **Unechte Favoriten-Sortierung.** `TvV1LegacyUiModels.watchlist` setzt `addedAtUtc = null` für jede Karte. `TvWatchlistScreen` sortiert „Neueste“/„Älteste“ gerade nach diesem immer fehlenden Datum, dann teils alphabetisch. Datumsbasierte Sortierung braucht einen **tatsächlichen** serverseitigen Sortiervertrag bzw. Datum; bis dahin Label nicht so darstellen, als sei die Zeit bekannt. Auch globales Sortieren nur der bereits geladenen Seiten ist inkorrekt.
2. **Bücher und Manga in der TV-Favoriten-UI.** `watchlistPage` liest unbeschränkt alle Typen. Die V1-Detailroute lehnt Nichtvideos ab; Karten öffnen dann lediglich Info statt echter Wiedergabe. Produktentscheidung: TV als Video-App konsequent filtern, oder explizite Informationsansicht für nicht unterstützte Medien anbieten. Der BOOKS-Filter bleibt derzeit sichtbar; Sammlungen dagegen sind video-gefiltert.
3. **Starre 2.000er-Grenze.** `TvV1CatalogHost.loadCatalogPages` (Personen-Filmografie) und `TvV1ClientFlow.allPages` (Film-Availability) laden höchstens 20×100 Elemente und schlagen danach fehl. Home/Search/Favoriten besitzen besseres on-demand Paging. Einheitliche paginierte Detail-Listen statt sofortiger Vollauslastung.
4. **Server-Discovery fehlt im V1-Wizard.** Das ursprüngliche `TvAppHost` verwendet `TvServerDiscoveryClient`, im V1-`TvSetupScreen` werden `discovered` und `discovering` jedoch nicht übergeben. Manuelle Server-URL funktioniert, automatische Erkennung in der V1-Laufzeit nicht.
5. **Discover-Requests fehlen.** Externe Titel können korrekt als Provider-Details geöffnet werden; kein sichtbarer TV-Request-/Wanted-Flow oder passende API-Integration im V1-Screen. Separat entscheiden, ob zur ersten TV-Version gehörend.
6. **Veraltete UI-/Architekturkommentare.** `clients/android/README.md` behauptet weiterhin pauschal `/api/client/v1` als einzigen Vertrag; `TvSearchFilter.kt` beschreibt noch reine lokale Suche und `TvMainActivity`/V1-Host Kommentare sprechen von noch zu verkabelnder Oberfläche. Keine Laufzeitfehler, aber irreführend bei Wartung.
7. **Playback-/Session-Finalisierung.** Der normale V1-Exit schreibt finale Progress-Sessions; der `DisposableEffect` räumt lokal den Player auf. Verhalten bei Prozessabbruch, Profilwechsel, HLS-401 nach Tokenrotation, Offline/NAS-Fehler und nicht bestätigtem Final-Write ist nicht hardwareverifiziert.
8. **Updatepfad unvollständig verifiziert.** Manueller Check/Download/Install-Dialog ist aktiv, aber `versionName=0.1.0-dev`, `versionCode=1` und Release-APK-Signierung/Upgrade-Pfad sind noch kein freigegebener Produktionsrelease. Alte automatische `TvUpdatePromptOverlay`-Anbindung fehlt in V1.
9. **Gemischte Navigation/Labels.** `TvSidebar` benutzt alte `TvRoute`-Werte und V1 hat separate `TvV1CatalogPage`; derzeit werden sie manuell gemappt. Search/Discover haben keinen direkten Sidebar-Einstieg. UI/Fokus/Back physisch testen.
10. **Fehlerfeedback/provider handling.** Home-Discover-Fehler werden absichtlich isoliert und der lokale Katalog bleibt nutzbar; ein sichtbarer Status „Empfehlungen gerade nicht verfügbar“ fehlt. Prüfen, ob gewünscht.

### Bewusst nicht im TV-v1-Release

- Lernen und wortbasierte interaktive Untertitel.
- TV-Downloads und Offline-Wiedergabe.
- Chronologischer Playback-Verlauf: #1504 (Next).
- Externe Online-Untertitel: #1505 (Next).
- Bildbasierte/PGS-Untertitel: noch kein passender Video-V1-Vertrag.

### Eigenständiger offener P0: Phone-Companion #1508

Der alte `TvCompanionRuntime`/QR-/SignalR-Pfad ist **nicht** in V1 verdrahtet, und die passenden Profil-/Playback-kompatiblen V1-Fernsteuerungsrouten fehlen. Kein heimlicher Rückfall auf den alten Cookie-Hub. Für einen TV-Release *mit* Handy-Fernbedienung sind API, Auth, Phone-UI, TV-Overlay und E2E vollständig umzusetzen.

## 5. Empfohlene Umsetzung, getrennte Arbeiten

**Paket A – vor erstem TV-Merge:** Anime-/Video-Subtype-Filter und Regressionstests; Favoriten-Sortierung korrekt darstellen/implementieren; TV-Video-/Bücher-UX festlegen; aktuelle main-/V1-Endpunktverträge testen; Android CI.

**Paket B – Legacy-Entflechtung:** Alle alten `TvAppHost`-`TvAppController`-`TvClientFlow`-Dateien erst nach Referenz-/Phone-Abhängigkeitsaudit aus TV-Produktionsquellen entfernen oder isolieren. `TvNavigation`, `TvScreens`, `TvSessionStore` vorher auftrennen, gemeinsam verwendete Compose-Screens erhalten. Alte Legacy-Tests mit dem stillgelegten Code entfernen/ersetzen, V1-Tests behalten. README/Komponentendokumentation aktualisieren.

**Paket C – fehlende Releasefunktion:** Phone-Companion #1508 auf eigenem Backend-/Phone-PR, sofern zur ersten Version gehörend; manuelle vs automatische Servererkennung und TV-Requests entscheiden; Filmografie/Asset-Paging; Release-Signing & Upgrade.

**Paket D – physisches TV E2E:** Vorhandenen 18-Fälle-Plan vollständig mit echtem Backend/Medien erfüllen: Profil-/Accounts/Keystore, Wiederherstellung, Anime, Film, Serie, D-Pad/Back, HLS/Direct, ffmpeg, Audio/Subs, Kapitel, Trickplay, Vorher/Nächste/Autoplay, Fortschritt, NAS/WOL, Prozessabbruch, 100+ Favoriten, Provider-Fehler. Fehler und Regressionsfixes getrennt dokumentieren.

## 6. Release-Aussage

**Android CI für die Quelle #1468 ist grün.** Eine bestandene Android CI des Main-Integrationsbranches ist ebenfalls kein Nachweis echter Playback-/D-Pad-Funktion. Erst nach P0/P1-Korrekturen, aktuellem Main-Contract-E2E, Gerätetest und Release-Signierung zu „merge-/releasebereit“ wechseln.

**Keine Dateien allein wegen Namens `Legacy` löschen und keine gestapelten Quell-PRs erneut mergen.**
