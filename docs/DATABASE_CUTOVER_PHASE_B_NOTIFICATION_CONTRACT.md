# Phase B — Notification-Zielvertrag

Quellen: PR #842, #836–#838 und die zugehörigen Notification-Specs. Ohne Learning.
Ziel für C/D/E, keine umgestellte Runtime. Service autorisiert READs; Logic schreibt
atomar. Keine Store-Schicht, zweite Queue oder zweites Event-Ledger.

## Persistenz

- `Events` belegt Occurrences. Namespaces `event:<uuid>` und `dedup:<domain-key>`
  verhindern eine Kollision von Replay-Identität und Gruppierung.
- `Notifications` hält Empfänger, Chronologie und Read/Dismissal. `NotificationEvents`
  erfasst jede Occurrence einmal. Deferred Constraints prüfen Zähler, letzte
  Occurrence und Source-Event. Ein Replay verändert keinen Read-/Dismissal-Status.
- `NotificationSchedules` gehört zum bestehenden Präferenz-Aggregat. Es speichert
  Popups, Ruhezeiten und Digest-Uhrzeit. Nur `Profiles.TimeZone` ist die aktuelle
  Zeitzone: erkannte kanonische Zone, kein UTC-Offset und kein zweites Setting.
- ISO-Wochentage 1–7 sind Kalenderwerte, kein Persistenz-Enum. Alle sieben bedeuten
  täglich, einer wöchentlich, mehrere eine Wochentagsauswahl. Keine Cron-Strings.
- Aktivierter Digest braucht Uhrzeit, Wochentage und externe Kanalwahl. Sink-
  Verfügbarkeit bleibt unabhängig; ihr Wegfall löscht keine gespeicherte Wahl.
- Digest-Abschaltung und ausdrückliches Umstellen auf Sofort/Deaktivieren schreiben
  Config und betroffene Subscriptions im selben Tx. Cancel verändert nichts.
- `NotificationDigestBatches` identifiziert eine geplante lokale Tages-Occurrence.
  `NotificationExternalOccurrences` entscheidet mit einem eindeutigen Profil/Event-
  Schlüssel genau einmal zwischen Sofort und einem Digest-Batch. Eine echte FK
  bindet Sofort-Routen an diese Entscheidung; parallele Tx können sie nicht doppelt
  vergeben. Digest-Mitgliedschaft bleibt im Capture-Fenster. Keine
  leeren Send-Routen, keine zweite Profil-Event-Erfassung und kein gleichzeitiges
  externes Sofort-/Digest-Senden derselben Occurrence.
- `NotificationDeliveries` enthält Route und Ergebnis. Schedule/Claim/Lease/Retry
  bleiben in der verknüpften `Operations`-Occurrence. Getrennte Kanäle/Push-Ziele
  haben eigene Ergebnisse; Erfolg auf einer Route wird nicht durch einen anderen
  Retry erneut gesendet. Keine Kopie des Medien-/Import-Operations-Inputs.
- `RecipientProfileId` ist der ausdrückliche Zustell-/Präferenzkontext, nicht die
  Event-Audience. Persönlicher Scope bleibt identisch. Admin-Events behalten
  Account-Audience; extern gelten aktuelle Rolle und eigener Profilkontext.
- `NotificationPushEndpoints` bleibt profilgebunden: intern bigint, outward UUID,
  SHA-256-Fingerprint für Dedup, ausschließlich geschützte Subscription-Payload.
  Ein dynamischer TransportKey bezeichnet einen tatsächlich registrierten Adapter,
  nicht einen Metadata-Provider. Optionale Sessionbindung gehört zum selben Account.

## Scheduling, Transport und Retention

Inbox-READs sind vor eindeutigem Root-Paging autorisiert und geben öffentliche
UUIDs aus. Interne Scheduling-/Route-READs betreffen genau eine vertrauenswürdig
aufgelöste Delivery. Verfügbarkeit kommt aus registrierten/konfigurierten Sinks,
nicht aus einem Client. Keine Secrets im outward DTO oder Diagnose-Log.

Vor I/O aktuelle Route und NextAllowedAt lesen, Policy prüfen und den DB-Tx schließen.
Ruhezeiten verschieben externe Aufmerksamkeit; In-App und Critical-Bypass bleiben
sofortig. Verschiebung schreibt Operations.NextAttemptAt und verändert keine lokale
Digest-Uhrzeit. Nach Settings-/Timezone-Änderungen werden offene Termine neu berechnet.
PostgreSQL-Zonenauflösung ist der DST-Vertrag: fehlende lokale Zeit rollt durch die
Lücke vorwärts, eine doppelte Endzeit nutzt die spätere Standardzeit-Occurrence.
Cross-Midnight, Gap und Fold sind auf PostgreSQL mit Europe/Berlin geprüft.

Vor jedem externen Versuch revalidieren: Enabled/Role/Membership, Event-/Kanalwahl,
Digest-Config, verifizierte E-Mail, Endpoint-/Session-Revoke und Sink-Verfügbarkeit.
Erfasste Digest-Events bleiben bei weiterhin aktivem Digest im ursprünglichen Batch,
wenn spätere Occurrences auf Sofort umgestellt werden. Deaktivierte Routen senden
nicht mehr. Ein Sinkfehler retryt nur die Route, niemals die Source-Operation.

Externes Exactly-Once benötigt einen realen Sink-Idempotency-Vertrag. Ohne diesen
darf ein ungewisser Send/Crash-Ausgang weder als sicher zugestellt noch als sicher
wiederholbar gelten. SMTP-/Push-Konfiguration wird nur mit dem tatsächlichen
Adaptervertrag ergänzt; keine Fake-Sinks oder freie Config-JSON-/EAV-Schattenstruktur.

Read/Dismissal löscht keine Events. Retention räumt abhängige Inbox-/Route-Diagnostik
explizit im Tx auf; Event-Audit und Operations haben getrennte Retention. Banner
bleibt aus aktuellem kanonischem Domain-Zustand abgeleitet, nicht aus Inbox-Status.
