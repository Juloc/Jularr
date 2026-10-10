# Phase B: Learning v2 Scope-Inheritance SQL (fokussierter Abschluss B05)

## Fachlicher Nachweis

Bindende Quellen: `docs/LEARNING_V2.md` (Hard Gates, Scope-Kette, Capability-Presets),
`docs/LEARNING_LANGUAGES.md` (nur ein LearningCards-/Review-State-Owner) und
`src/Jularr.Web/Features/Learning/LearningConfiguration.cs` (bereits ausgeführte
Profile/MediaType/Work/Content-Scopes, LearningMode, 14 Capabilities).

Das bisherige Zielmodell hatte LearningCourses/LearningCards/FSRS/Curriculum, aber
**keinen** expliziten relationalen Zielowner für die bereits existierenden, vom
Benutzer konfigurierbaren Learning-Modi und Capability-Overrides auf vier Ebenen.
Dafür ergänzen wir nur zwei persistierende Tabellen und drei fachlich echte
`enum : byte`-Kataloge im isolierten **DDL-Entwurf 13**. Es gibt keinen zweiten
Learning-Progress-/Card-/Event-Owner und keine neue Service-/Store-Schicht.

- `LearningScopeOverrides`: `ProfileId bigint`, optionale Learning-Media-Klasse,
  `WorkId bigint`, optionale `WorkEpisodeId/WorkChapterId bigint` (composite
  Work-membership FKs), optionaler Mode-FK und `UNIQUE NULLS NOT DISTINCT` auf
  den gesamten Scope. NULL Mode heißt geerbter Modus; kein JSON/EAV-`TargetType+Id`.
- `LearningCapabilityOverrides`: eine boolesche, explizite Entscheidung je
  `LearningScopeOverrideId` und `LearningCapabilityTypeId` (keine persistierte
  doppelte Menge effektiv aufgelöster Capabilities).
- `LearningModeTypes`: belegte Quellwerte Off=0, LanguageTools=1, Study=2,
  Custom=3.
- `LearningCapabilityTypes`: die 14 belegten Quellwerte 1..14.
- `LearningMediaScopeTypes`: belegte Quellwerte Anime=1, Novel=2, Book=3,
  Manga=4. **Diese Werte sind nicht `MediaTypes`!** Anime ist weiterhin
  Klassifikation eines Works, kein separater Work-Root.

Die Mode/Capability-Zahlen sind anhand aktueller `LearningConfiguration.cs`
belegt und als **vorgeschlagene neue Byte-Seedwerte** im Draft verwendbar. Die
zugehörigen neuen `enum : byte` und endgültige Namens-/Seed-Signatur B01
sind erst nach zentralem Vertrag freigegeben, nicht durch den PG-CI-Test.

## Autorisierungs- und Auflösungsregeln

Vor Lesen/Schreiben: Instanz-Learning-Modul, effektive Profile-Berechtigung
und **persönliche** `Profiles.IsLearningEnabled` müssen zugelassen sein. Der
letzte Schalter wird parallel in PR #949 auf dem Phase-B-Branch ergänzt; dessen
Dateien **nicht** durch diesen PR überschreiben. Ein Child-Scope kann ihn nie
reaktivieren. `LearningMode` nach spezifischster Scope-Stufe, Capabilities
nach ihrer jeweiligen On/Off-Override-Kette breit→eng. Effektive Ergebnisse
nur per Service-Read berechnen; keine separate Persistenz für „effective“.

`LearningMediaScopeTypeId` ist eine **Learning-UI-Klasse**, weder ein
`Works.MediaTypeId` noch ein allgemeines Permission-Token. Das Work gehört
zur spezifizierten Learning-Klasse und ggf. Anime-Klassifikation; dies wird vom
autorisierten Service/Logic anhand der zentralen Work-Daten geprüft. Der SQL-FK
erzwingt für einzelne Chapters/Episodes die richtige Work-Mitgliedschaft und
verhindert fremde Unit-Referenzen. Für zusätzliche Contentformen jenseits der
aktuell konkret relationalen Episodes/Chapters vor Gate B einen nachgewiesenen
Fachschlüssel/Owner wählen; keine generischen freien ScopeKeys des alten Code
ungeprüft behalten.

## Isolierter Prüfnachweis und Integration

`DATABASE_CUTOVER_PHASE_B_PG_LEARNING_SCOPE_TESTS.sql` testet
Root/Media/Work/Content-Hierarchie, getrennte Profile, geerbten Mode,
14 Capabilities, gültige FK-/CHECK-/UNIQUE-Sets und Ablehnung von
fremden WorkChapters, doppelten NULL-Scope-Zielen und falschen Typcodes.
Alle Fixtures werden auf `phase_b_scratch` zurückgerollt.

**Wichtige Koordination:** PR #949 bearbeitet derzeit
`DATABASE_CUTOVER_PHASE_B_TABLE_MANIFEST.csv`, `...ENUM_SEED_MANIFEST.csv`
(ggf. nachziehen), `...DECISIONS.md`, `...SCHEMA.md` und viele
Bestands-DDLs. Dieser eigenständige Scope-Patch ändert keine dieser
fremden Dateien. **Nach #949-Merge** müssen die 5 neuen Tabellen und 3
neuen Type-Kataloge im konsolidierten Manifest registriert und die
gesamte DDL erneut aus isolierter PG-CI validiert werden. Die 173 Tabellen
sind deshalb nur ein Teststand, kein Ziel für die endgültige Anzahl.

**Gate B bleibt insgesamt offen** für den ID-/Token-Agenten, Enum-/Seed-
Freigabe aller Catalogs, AI, Arr/Import, Provider-/Auth-Rechte und
Feature-Lücken. Die einzige EF/PG-Neuinstallationsbaseline ist Phase C,
der aktive Datenbankreset Phase G nach eigenem GO.
