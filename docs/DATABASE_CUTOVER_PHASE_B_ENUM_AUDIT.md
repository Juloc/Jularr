# Phase B – geprüfte bestehende Enumwerte (kein finales Target-Seed-GO)

**Ziel:** B01 konkret abarbeiten statt erfundene numerische Seedwerte als produktionsfertig auszugeben. Nachweis aus `dev`-Quelldateien vom 10.10.2026. Die meisten alten C#-Enums sind standardmäßig `int`; die neue SQL-Baseline verlangt für persistierte Ziel-Enums die später explizit vereinbarte `enum : byte` ↔ PostgreSQL-`smallint`-Bindung. **Nicht 1:1-Altwelt konservieren.**

| Quelle | Bestehende Enumwerte | Bedeutung für neue DDL | Stand |
| --- | --- | --- | --- |
| `Features/Auth/OwnerAccount.cs` / `AccountRole` | Owner=1; User=2; MediaManager=3 | `AccountRoleTypes`-Draft korrigiert: zuvor vertauschte User/MediaManager-Seeds wurden berichtigt. | **Codewert belegt, neuer Byte-Vertrag noch offen** |
| `Features/MediaCore/MediaCoreEnums.cs` / `WorkMediaType` | Movie=0; Series=1; Anime=2 (historisch; reine Klassifikation); Book=3; Manga=4; LightNovel=5; Music=6. Kein alter Game-Wert. | `MediaTypes`-Draft hat absichtlich einen anderen, vorgeschlagenen **neuen** Satz mit Game und ohne Anime. **Alte Zahlen nicht übernehmen**; alle neuen IDs final in einem Manifest festlegen. | **Neue Target-Seeds bewusst noch nicht freigegeben** |
| `Features/Notifications/NotificationModels.cs` / `NotificationMode` | Off=0; InApp=1; Push=2; Digest=3 | Diese Werte bilden den bestehenden UI-/Transportstatus ab, **keine** Bestätigung, dass Push/Digest funktionieren. `NotificationChannelTypes` und `NotificationTimingTypes` sind separate fachliche Dimensionen; nicht einfach `NotificationMode` kopieren. | **Codes belegt, Target-Kanäle offen** |
| `Features/Learning/Courses/LearningCourseModels.cs` / `LearningUnitKind` | Word=1; Sentence=2; Script=3 | `LearningUnits.KindKey` in Draft 08 muss vor Baseline als typisierter, freigegebener Key/Type-FK entschieden werden. | **Source belegt, DDL-Korrektur offen** |
| Gleiches File / `LearningCardMode` | Recognition=1; Production=2; Listening=3; Writing=4 | `LearningCardModeTypes` muss diese Schlüssel erhalten, explizit neue byte Werte freigeben. Die vorhandene Recognition ist der Anchor für den Unit-State. | **Source belegt, Ziel-Enum noch nicht signiert** |
| `Features/Acquisition/Quality/QualityModels.cs` / `ReleaseRuleEffect` | Prefer=0; Avoid=1; Require=2; Reject=3; Info=4 | `AcquisitionRuleEffectTypes` vor Seed an die künftige C#-Schnittstelle binden. | **Source belegt, Target-Seed noch offen** |
| Gleiches File / `ReleaseRuleMatch` | Equals=0; Contains=1; Regex=2 | `AcquisitionRuleMatchTypes`: Regex-Auswertung braucht Sicherheits-/Timeout-Budgets. | **Source belegt, Target-Seed noch offen** |

## Der noch zu erfüllende B01-Abnahmevertrag

1. Für **jede** persistierte Type-Tabelle: exakte Key-Strings, `smallint`-ID, zugehörige **neue** C# `enum : byte` oder explizite kontrollierte Nicht-Enum-Bindung und Versionierung in **einem** Seed-Manifest festschreiben.
2. Verhindern, dass alte int-Codes **versehentlich** neue Semantik bekommen: Runtime- und Migration-Adapter müssen bewusst zwischen altem und neuem Schema unterscheiden. Der Reset erfolgt später, nicht in Phase B.
3. FK-seitig unterbundene ungültige Typkombinationen testen (insbesondere ImageType/TargetKind, AcquisitionRuleEffect, ProgressPositionType, GameRelease-WorkMediaType).
4. Typ-Tabellen ohne Seed **nicht** als einsatzfähiges Schema bezeichnen; fehlende Seeds und Prerequisites im Manifest bis zur fachlichen Signatur markieren.

**Kein allgemeiner Produktentscheid aus historischen Quell-Enumnummern abgeleitet.** Nur eindeutig belegte Sourcewerte stehen oben. Vor echten PostgreSQL-/EF-Tests kann Gate B nicht grün werden.

**Vollständige Ziel-Type-Inventur:** [DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv](DATABASE_CUTOVER_PHASE_B_ENUM_SEED_MANIFEST.csv) erfasst **alle 41** in den zehn DDL-Entwürfen vorgesehenen `...Types`-Tabellen. Nur **fünf** davon besitzen im aktuellen Entwurf überhaupt vorgeschlagene `(Id,Key)`-Seeds; die übrigen sind ausdrücklich **nicht einsatzbereit**, bis echte Byte-Enum-/statische-Katalog-Werte definiert sind. Der Audit alter C#-Werte oben ist ein Beleg, keine automatische Zielseed-Freigabe.
