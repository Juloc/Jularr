# Phase B – AI-Backend- und Präferenzpersistenz

**Quelle:** `docs/mockups/admin-ai/SPEC.md`, `Features/Ai/AiProfileSettings.cs`,
`AiProfileSettingsStore.cs`, `AiModelCatalogStore.cs`, `AiUsageStore.cs`
sowie `AiOperations`. Die bestehende App legt persönliche Einstellungen
derzeit geschützt unter `/data/integrations/ai/accounts` ab und hat bereits
den kanonischen `AiUsageDaily`-Inhalt und ein Provider-Model-Catalog.
Der neue Clean-Cut entwirft deshalb **einen** Zielowner pro Fakt, **ohne**
die alte Testinstanz oder ihre geschützten Secrets zu migrieren.

## Geprüfte Datenverantwortungen

- `AiServerProviders`: Admin-konfigurierbare **instanzweite** Adapter,
  Endpoint, Status/Priorität, Verweis auf separat geschützte Credentials.
- `AiModels`: einziges lokal gespeichertes Server-Model-Catalog, Provider-
  und NativeModel-Unique, capability metadata nur als begrenzte,
  versionierbare **Provider-Evidence**, nicht als freie Domain-Fakten.
- `AiTaskRoutes`: **eine** Primary-/Fallback-Routingregel pro stabilem
  `AiOperations`-Task-Key; aktive Route erfordert Primary, Primary darf
  nicht zugleich der Fallback sein.
- `AiProfileSettings`: persönliche Providerwahl und Budgets unter genau
  dem zugehörigen `ProfileId bigint`. Keine rohe persönliche API-Key-Spalte,
  sondern `CredentialStorageKey`, getrennt vom serverweiten Provider.
- `AiProfileOperationOverrides`: nur Abweichungen vom Profil-Default,
  gebunden an den Profilsettings-FK.
- `AiUsageDaily`: lokale gemessene/geschätzte Nutzung pro
  Profil/UTC-Tag/Provider/Model/Operation mit 16 **nichtnegativen**
  Tageszählern. Keine Token-IDs, Secrets oder zweite Operations-Queue.
- `AiTranslationModeTypes`: source-belegt
  Efficient=0, Quality=1, Maximum=2, FK/Seed für neue C# `enum : byte`.

**Kanonische Abgrenzung:** `Operations` bleibt der einzige durable
Queue-/Claim-/Retry-Owner für asynchrone KI-Arbeit. `Images`,
`ReaderContent`, `WorkMetadataFacts` bzw. bestehende
Übersetzungsowner besitzen die generierten Resultate. AI-Tabellen
duplizieren weder diese Inhalte noch ihre Work/Progress-Identität.

## Autorisierung und Sicherheit

Instanzweite Admin-Provider/Routes nur mit Admin-Berechtigung. Persönliche
Einstellungen und Tokenbudget ausschließlich mit autorisiertem
`ProfileId`; die Service-/Logic-Grenze aus PR #941 gilt. Ein
`CredentialStorageKey` ist ein **nicht-geheimes Handle** auf
DataProtection/SecretStore, kein Credential selbst. Kein unredigierter
Provider-Error/Prompt/Payload/Stacktrace in täglicher Nutzungsstatistik.
Validierung der vertrauenswürdigen `AiOperations`-Keys und HTTP-Endpunkte
liegt in Logic/Service; SQL darf keine frei interpolierten Tasks zu
Query- oder Provider-Routen machen.

Client sichtbare Ressourcenschlüssel sind niemals diese internen
`bigint` IDs. Die Instanz-provider `Key` und Task-`TaskKey`
sind autorisiert abfragbare maschinelle Referenzen; echte API-/Bearer-
Tokens sind separate Credentials. Keine zweite Global-User-AI-Policy,
sondern bestehende Permission-/Instance-Module-Gates.

## Isolierte Abnahme

Der `15_AI_DRAFT.sql`-Teil erzeugt sieben Tabellen ohne neue
Server-API/Runtime-Mutation. `PG_AI_TESTS.sql` belegt gültige Routing-
und Usage-Fälle sowie negative FK/UNIQUE/CHECK-Fälle, darunter
ungültige Übersetzungsmodi, leere Overrides, falsche Providerwahl,
negative Usage-Zahlen und doppelten Tageskey. Die Scratch-PG18-CI
muss Parts 01..15 nacheinander anwenden, jetzt **180 Tabellen**
und **47 Type-Kataloge** (noch KEIN endgültiger Zielwert).

**Nicht mit Gate-B-Freigabe verwechseln:** Profile-/Admin-Policy-Races,
Netzwerkprovider-Sicherheit, echte Budget-Concurrency und Budgetaggregation,
Model-Routing-Fallback und AI-Ergebnis-Publish sind Logic-/Service-
Integrationspflichten in D/E. Das endgültige :byte-Typmanifest und weitere
Fachbereiche sind vor **Gate B** weiterhin zu prüfen. EF-Migrationsbaseline
ist Phase C.
