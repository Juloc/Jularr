# Phase A — verifizierter Prüfstand vom 10.10.2026

**Gate A: OFFEN. Phase A ist noch nicht vollständig abgeschlossen.** Die maßgebliche lokale Entwicklungsdatenbank wurde ausschließlich lesend geprüft. Der vollständige lokale Sourcebestand ist indiziert; diese statische Abdeckung ist keine Abnahme aller fachlichen und autorisierten Aufrufketten. Es gibt keinen fehlenden PostgreSQL-/Repository-Zugriff mehr. Die unten bezeichnete verbleibende Prüfung und Abstimmung ist tatsächlich noch auszuführen.

Der Auftrag ist ein **Clean Cut**: sämtliche Zielstrukturen werden neu entworfen und in genau einer neuen Baseline angelegt. Kein Backfill, keine Übernahme der alten Migrationskette und keine dauerhafte Kompatibilität. `KEEP` in der Matrix bedeutet ausschließlich Erhalt einer fachlichen Verantwortung; weder vorhandene DDL noch Daten werden damit zum Ziel übernommen. Exakte Ziel-DDL ist Phase B und wurde nicht implementiert.

## Quelle, Installation und Koordination

- Geprüfte `origin/dev`-Quelle: `e6c3c77ac8f9f41d7b6324a06c29cb772bcc68ee`; Epoch und Minimum jeweils **4**. Vor Veröffentlichung erneut gefetcht, unverändert.
- Bestehender PR [#945](https://github.com/Juloc/Jularr/pull/945), Branch `docs/cutoff-phase-a-inventory-20261010`, isolierter Worktree. Keine fremden Änderungen übernommen.
- Der Nutzer hat die UI-Demo als maßgebliche dev-Installation bestätigt: HTTP `127.0.0.1:5230`, PostgreSQL `127.0.0.1:5433`, Datenbank `jularr_demo`, Container `jularr-demo-postgres`, PostgreSQL **18.6**. Laufender Web-Prozess und seine TCP-Verbindung wurden dieser Installation zugeordnet. Web-Build aus UI-Worktree #944, Head `9147d6cd704f7a42ce14e57b4fcb882cef3c42e4`; EF-Produktversion der angewandten Historie **10.0.12**. Dieser UI-Stand ist nicht identisch mit `origin/dev`; sein PR-Delta ist separat erfasst.
- Katalogaufnahme **2026-10-10 13:45 UTC**: `BEGIN READ ONLY`, 30 Sekunden Statement- und 3 Sekunden Lock-Timeout, Katalog-SELECT plus Migrationshistorie, danach `ROLLBACK`. Keine Produktdatensätze, Token oder Passwörter exportiert. Der Aufnahmezeitpunkt bleibt getrennt vom späteren Reconciliation-Zeitpunkt.
- Agent-Control-Claim [6098067132](https://github.com/Juloc/agent-control/issues/1#issuecomment-6098067132), präzisierter Scope [6098342938](https://github.com/Juloc/agent-control/issues/1#issuecomment-6098342938). Backend #943 und UI #944 hatten ihre getrennten Claims freigegeben; kein aktiver überlappender Inventar-Claim festgestellt.
- Primary Worktree: lokaler `dev`-Head `925467efb186971370a748a824cf602b7de0f465`, ungepushte Discovery-/Admin-Mockup-Änderungen. Diese wurden nur als Pfad-Delta erfasst und gehören **nicht** zur fixierten Source-Abnahme. Requests-UI-Worktree `b06dc9f3028624e549fa820eeae92f0b01a993bf` und UI-Foundation-Worktree waren sauber. Exakte Heads/Pfade in `_phase_a_search_sql_patterns_3.json`.

Verbindliche Quellen bleiben [Übergabe #880](https://github.com/Juloc/Jularr/issues/880#issuecomment-6098004941), [Sequenzplan / #941](https://github.com/Juloc/Jularr/blob/docs/service-data-logic-contract-20261010/docs/DATABASE_CUTOVER_EXECUTION_PLAN.md), dessen Service/Data/Logic-Vertrag und `CLEAN_CUT_DATABASE.md`. Diese Datei protokolliert den Prüfstand; die CSVs sind die reviewbare Matrix.

## Belastbar gezählte Abdeckung

| Nachweis | Ergebnis | Bedeutung |
| --- | --- | --- |
| Live-Tabellen | **152 Anwendungstabellen + 1 Framework-Tabelle** | Alle 153 in Tabellenmatrix und Katalog enthalten |
| Spalten | **1.421 Anwendungsspalten; 1.423 insgesamt** | Live-Typen, Länge/Präzision, NULL, Defaults und Identity erfasst |
| Constraints / Indizes | **1.351 / 426** | PostgreSQL 18 zählt auch NOT-NULL-Constraints; keine 1.351 fachlichen CHECKs behauptet |
| Extensions / Funktionen | **2 / 31** | `pg_trgm 1.6`, `plpgsql 1.0`; 31 öffentliche Extension-Funktionen, keine fachliche Stored Function festgestellt |
| Views / eigene Trigger / PG-Enums | **0 / 0 / 0** | Kein Beleg für solche aktiven Objekte; interne FK-Trigger nicht als eigene Trigger gezählt |
| Migrationen | **56 / 56 angewandt** | Alle `Up()`-Klassen mit Live-Historie abgeglichen; alte Folge wird nicht importiert |
| Tabellenmatrix | **201 Namen** | 156 ursprüngliche Namen + Framework-Historie + 44 zusätzliche Zielnamen; 75 im Zielvertrag erwähnte Namen einschließlich 2 bedingter Tabellen |
| Lokale Source-Bodies | **2.045 Pfade**, davon **960 C#** | Nichtmigration-Dateien unter src/clients/tests; Tests sind eigene SourceKind, keine Produkt-Consumer-Zahl |
| Bootstrap/Build-Konfiguration | **13 zusätzliche Pfade** | Compose, Projektdateien, Workflows, Epoch und vorhandene Skripte; insgesamt 2.058 indexierte Pfade |
| Statische Zugriffsstellen | **2.263 in 243 Dateien** | 1.662 semantische DbSet-/Set-Referenzen + 601 SQL-/Save-/Transaktions-API-Stellen; nicht 2.263 fertige Workflows |
| Lokaler Aufrufgraph | **12.030 deklarierte Symbole / 13.412 Source-Kanten** | 340 nicht gebundene Aufrufe; DI, Delegates und dynamische Dispatches benötigen Ergänzung |
| Razor / Feature-Referenzlisten | **150 / 236 Pfade** | Union aus tatsächlichem lokalem AppDbContext-Textbezug und direkten Zugriffsstellen; DI/Views bleiben im Inventar, auch ohne direkte DML |
| Native Verträge | **44 Zeilen** | 34 Konstanten/Routenkonstruktoren inkl. Protokollmetadaten + 10 Session-/Hub-Verträge; keine Behauptung von 44 eindeutigen URLs |
| Manuell verfolgte Fachpfade | **12** | Selektive Review-Evidenz mit Entry, Scope und Zielowner; keine Vollabnahme sämtlicher Methoden der Datei |
| Offene PRs | **17 einschließlich #945** | Alle ChangedFiles vollständig paginiert und Head-SHAs fixiert; vor Veröffentlichung erneut geprüft, kein Head-Delta |

Die vier bestätigten historischen/abwesenden Namen sind `WorkIdMigrationMap`, `MediaFiles`, `MediaAnalyses`, `MediaAnalysisStreams`. **`MediaProgress`, `MediaPlaybackHistory` und `ActiveSessions` sind aktiv.** Snapshot-ToTable-Namen allein decken die DB nicht ab. Historische Migrations-SQL-/Up-Evidenz bleibt erhalten; der alte Raw-Schema-Snapshot wurde durch den Live-Katalog ersetzt, keine zweite konkurrierende Inventur angelegt.

Die Roslyn-Analyse nutzt das vorhandene SDK und vorhandene Abhängigkeits-DLLs, ASP.NET **10.0.12**. Native DLLs werden von Metadata-Referenzen ausgeschlossen. Im Audit sind ausschließlich **229 CS8795** aus nicht ausgeführten GeneratedRegex-Sourcegeneratoren übrig; das ist keine fehlgeschlagene Produktkompilierung und auch kein erfolgreicher Produkt-Build. SQL-Tabellennamen aus Literalen sind Kandidaten; gleichnamige konstante Felder, dynamisch übergebene SQLs und einzelne Lambdas sind nicht durch diesen Index abschließend aufgelöst.

## Fachliche Zuordnungen und PR-Delta

Die Tabellenmatrix enthält Live-Felder/FKs/Indizes, Zielaktion, Zieltabellen, Begründung, Fachowner, direkte Source-Symbole und Caller-Kandidaten. Gemeinsame Work-/Image-/Progress-/Account-Verantwortungen ersetzen die alten Medienroots. Die neuen Account-/Profile-/Auth-, Reader-, Game-, Image-/Detection- und Progress-Ziele sind rückwärts ebenfalls erfasst. `AccountPermissions` und `ImageGenerationPresets` bleiben die ausdrücklich **bedingten** Vertragspunkte; das Inventar behauptet keine bereits beschlossene Implementation.

Konkret verfolgte Pfade stehen im bestehenden `_phase_a_search_sql_patterns_3.json` unter `reviewedChains`: Admin-System-Razor, Plex Login/Link, profilbezogener Progress, paginierte Watchlist, Wanted mit dynamischen Sources/Handlers, Startup, generische Work-Kindtabellen, Companion-Sessions, TV-Device-Pairing, Musik-Spezialmodelle, Untertitelpolicy und dateibasierte AniList-Verbindungen.

Wichtige geprüfte Unterschiede:

- `SubtitleLanguageProfileItems.ProfileId` verweist auf eine **Untertitelpolicy**, nicht auf ein persönliches Profile. Gemeinsame Namenssuffixe dürfen nicht blind umgeschrieben werden.
- Musik-Album ist bereits eine `WorkId`-Erweiterung; Artist und mehrfach verwendbares Recording sind fachliche Spezialidentitäten. Game-Releases werden hingegen die freigegebene WorkVersion-Erweiterung; Release-Hash-Evidenz bleibt von File-Checksums getrennt.
- Companion-`PlaybackSessionStore` und Device-Pairing halten aktuell In-Memory-Zustand. Das ist zusätzlich zum SQL-`ActiveSessions`-Consumer zu behandeln; AccountSessions sind ein weiterer eigener Vertrag.
- Native `ApiVersion=2` und `/api/client/v1` sind getrennte Versionsverträge. HLS/Fallback, Offline-Grants, Work/Profile/unit-IDs, Cookie-/Teilnehmer-Sessions und gespeicherte Caches müssen in E gemeinsam wechseln. Reale Credentials/NAS-Dateien sind keine alten DB-Testdaten.
- Direkte Admin-/Plex-Razor-DML und GET-Pfade mit Probe/Session-Start gehören künftig hinter autorisierte Services und mutierende/IO-Logic. Pure Read-Service-SELECT darf diese Effekte nicht verstecken.

Entscheidungen in `DATABASE_CUTOVER_PR_DELTA.csv` sind **begründete Phase-A-Integrationsvorschläge, keine Merge-Freigaben oder erfundene Owner-Zustimmungen**:

| Gruppe | PRs | Festgehaltene Behandlung |
| --- | --- | --- |
| Vor Freeze integrieren | #941, #943, #944, #945 | Architektur, schemaunabhängige Runtime, Shared UI, Inventar; eigene Gates und Integration weiterhin erforderlich |
| Auf Ziel portieren | #937, #920, #929, #918, #917, #842, #768 | Provider, Playback, Native, Notification und Request-Policy müssen ihre erforderlichen Fähigkeiten behalten |
| Playback-Alternativen | #928, #923, #921, #919 | #929 als Integrationskandidat; Owner-Wahl und Übernahme nicht duplizierter Fähigkeiten noch unbestätigt |
| Separate Erweiterung | #761, #608 | Neue Account-Groups bzw. Presentation-Umstrukturierung; kein stiller Verlust bestehender dev-Funktionen |

Schema-Deltas: #920 `PlexLoginAttempts.Purpose`; #842 Channel-Tabellen und Preference/Timing/Enabled; #761 AccountGroups/AccountGroupMembers. Deren alte Migrationen werden nicht in die neue Baseline kopiert. Die neuen fachlichen Anforderungen müssen vor B-DDL-Entscheidung bzw. bewusster Feature-Ausnahme geklärt sein.

## Offene Abnahmekriterien — konkrete Restarbeit

`node scripts/cutover/verify.mjs --gate` **scheitert absichtlich mit Exit 2**, solange diese tatsächlichen Lücken bestehen:

1. **37 SQL-/Tracked-Zielstellen** sind nicht abschließend aufgelöst: insbesondere generische Transaktions-/Diagnose-/SQL-Helfer sowie SaveChanges nach indirektem Laden. `SEMANTIC_ACCESS.csv` filterbar mit `Tables` enthält `UNRESOLVED`. Scope-Operationen ohne direkte Tabellen sollen als solche fachlich bestätigt werden, nicht pauschal alle Tabellen bekommen.
2. **2.263 Zugriffszeilen und 1.027 relevante Consumer-Pfade haben noch keine vollständige Kettenabnahme.** Zwölf überprüfte Pfade ändern diesen Befund nicht. Jede restliche Zeile benötigt konkrete Caller/HTTP-Verb/DTO, Actor/Account/Profile/Resource/Module, Reader/Writer/Sideeffect und User/Admin/System/Sonder-Caller-Service. Aktuelle `Review`-Werte sind bewusst nicht `VERIFIED_AUTHORIZED_CHAIN`.
3. **61 Tabellen-Dispositionen sind noch Erhaltungsvorschläge**, nicht abschließend aus Fachcode freigegeben. Filter `DispositionReview=PROPOSED_DISTINCT_RESPONSIBILITY_REQUIRES_REVIEW`; insbesondere Learning-/Curriculum-/Capability-, Acquisition-/Wanted-, Notification-/Event-, Reconciliation-, Preferences und unterstützende Metadata-Verantwortungen. Auch die 157 IST-/Framework-/historischen Zeilen haben keine vollständige Feldabnahme. Der Live-Katalog belegt die Felder; er entscheidet ihre fachlichen Zielrollen nicht. Exakte DDL folgt erst B.
4. **PR-Abstimmung ist dokumentiert, aber 17 Zeilen ohne neue Owner-Bestätigung.** Playback-Integrationswahl, #842 Channel-Policy, #761 Feature-Extension und ungepushte Primary-Discovery-Änderungen müssen vor verbindlichem Integrations-/Schema-Freeze entschieden und nachdiffiert werden. Es wurde niemandem ein Merge unterstellt.

Die vier breiten Phase-A-Checkboxen in #880 bleiben deshalb offen. Erfüllt sind ihre Teilnachweise: Live-Katalog/History, fixierte Quelle/Epoch, vollständiger statischer Sourceindex und aktuelle PR-/Worktree-Deltas. **Nicht vollständig erfüllt** sind fachliche Feld-/Consumer-Zuordnung und verbindliche PR-Entscheidungen. Phase B ist nicht freigegeben.

## Prüfungen und Reproduktion

- `dotnet run --project scripts/cutover/SourceAudit.csproj -- <repo> <vorhandener Web-DLL-Ordner> <temporäres Audit-JSON>`: Auditor erfolgreich gebaut und ausgeführt; keine neue NuGet-Abhängigkeit.
- `node scripts/cutover/reconcile.mjs <Katalog-JSON> <Audit-JSON> <PR-JSON> <Worktree-JSON>`: Live-/Source-/Target-/PR-Matrix erzeugt; stoppt, wenn Produktquellen von `origin/dev` abweichen.
- `node scripts/cutover/verify.mjs`: Duplicate/Missing/Owner/Target/Source-SHA/History/Counts/C#-Zeilenlängen-Sanity **grün**. Das ist Inventarkonsistenz, nicht Gate A.
- `node --test scripts/cutover/verify.test.mjs`: **6/6 grün**; fehlende Live-Tabelle, doppelte Tabelle, gemischter Consumer-SHA und falsches grünes Gate werden zurückgewiesen.
- `npm ci`: ausgeführt, Repository-Autorschaftshooks aktiviert. Kein Bypass; reale konfigurierte Contributor-Identität.
- `ReadCatalog.sql` ist die exakte ausschließlich lesende Abfrage. Ausführen gegen die ausdrücklich bestätigte Installation, `psql -X -At -q -v ON_ERROR_STOP=1`, Resultat zunächst außerhalb des Repositorys. Keine credentials in Argumenten oder Evidenz veröffentlichen. Context-Inputs enthalten GitHub `pr list` plus REST-paginierte `pulls/N/files` und nur Git-Head/Status-Metadaten.

Kein Produkt-/Solution-Build, keine breite Testsuite, kein Android-Build, E2E, Deployment, Reset oder neue Baseline wurde ausgeführt. Das sind spätere Phasen; der Auditor-Build und sechs Guard-Tests ersetzen diese Gates nicht. Kein existierender DB-/Migrations-/Runtime-/NAS-/Credential-Pfad wurde verändert.