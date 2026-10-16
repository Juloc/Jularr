# Learning nach dem Cutover

Status: zurückgestellt durch aktuellen Owner-Auftrag vom 10.10.2026.
Keine Phase-B-Abnahmevoraussetzung, keine Tabellen oder Seeds der neuen Baseline.
Die bestehenden LEARNING_*.md-Dokumente bleiben Ideen-/Fachreferenzen für einen
später gesondert zugeschnittenen Ausbau, keine aktuelle Umsetzungsfreigabe.

## Grobe spätere Richtung

- Optionales persönliches Profilmodul, standardmäßig aus; Freigaben und
  Inhaltskontexte verwenden die dann vorhandenen Account-/Profile-/Work-Identitäten.
- Ein gemeinsamer Owner für Lernbegriffe/-einheiten, Sprachvarianten, persönliche
  Kurse, Karten und Reviews. FSRS-Zustand wird nicht je Medienart dupliziert.
- Medienkontexte verweisen typisiert auf vorhandene Episoden/Kapitel; kein zweiter
  Medienkatalog und kein Zusammenlegen von Medienfortschritt mit Lernfortschritt.
- Curriculum mit Blueprint → Level → Chapter → Lesson → Exercise erst als späterer
  Ausbau. Gemeinsamer Sprachpaarinhalt und persönlicher Fortschritt bleiben getrennt.
- Kleine Übungsfamilie: Presentation, MultipleChoice, Matching, Cloze, Ordering,
  ShortAnswer. Listening/Reading sind Modalitäten bzw. Lernziele, keine Parallelengines.
- Persönliche Lernaktivität; Gamification nur optional. Keine zweite globale
  Event-/Operations-Queue, kein XP-Schattenkonto und keine Heartbeat-Zeile pro Tick.

Diese Richtung friert weder Tabellenzahl noch Enum-Codes, vollständige Felder,
Publishing-, Capability- oder XP-Regeln ein. Vor Wiedereinführung werden Umfang,
kanonische Owner und fokussierte Tests erneut gegen den dann aktuellen Stand
festgelegt; Änderungen erfolgen als normale Vorwärtsmigrationen nach der Baseline.

## Konsequenz für den jetzigen Cutover

Phase B enthält keine Learning-DDL, -Typkataloge, -Reads oder Learning-Testgates.
Keine Platzhaltertabellen oder ungenutzten Profil-Opt-in-Spalten reservieren.

In D/E entfallen bestehende Learning-EF-Mappings, Bootstrap-/DI-/Job-Verbindungen,
Endpoints, Navigation und Learning-spezifische Player-/Reader-Werkzeuge gemeinsam
mit ihren ausschließlich hierfür vorhandenen Tests und Einstellungen. Kein
Legacy-/Fallback-Pfad bleibt als versteckte Abhängigkeit der neuen Baseline.

Unabhängige Reader-/Player-Funktionen, Untertitel, Übersetzung, AI-Aufgaben,
Medienfortschritt und UI-Lokalisierung bleiben erhalten. Der jetzige Beschluss
ändert weder laufenden Runtime-Code noch aktive Datenbanken. Ein tatsächlicher
DB-Reset bleibt der gesondert freizugebenden späteren Phase G vorbehalten.
