-- The Owner's default model for the server's translation as a provider-qualified id (`ollama:translategemma:12b`). NULL keeps the earlier choice:
-- the largest TranslateGemma model the first answering machine has.
ALTER TABLE "InstanceSettings" ADD COLUMN "TranslationModel" varchar(80) NULL CHECK ("TranslationModel" ~ '^[a-z][a-z0-9-]*:[^/]+$');

-- Which model's translation a block of a book shows, for every reader of it: a block re-translated with another model wins only there, the
-- section's other blocks keep the default model's text. Leaves with its reading document like the cached translations.
CREATE TABLE "ReadingTranslationChoices" (
    "ReadingDocumentId" bigint NOT NULL REFERENCES "ReadingDocuments" ("Id") ON DELETE CASCADE,
    "SectionIndex" integer NOT NULL CHECK ("SectionIndex" >= 0),
    "Language" varchar(3) NOT NULL CHECK ("Language" ~ '^[a-z]{2,3}$'),
    "BlockIndex" integer NOT NULL CHECK ("BlockIndex" >= 0),
    "Model" varchar(80) NOT NULL CHECK ("Model" ~ '^[a-z][a-z0-9-]*:[^/]+$'),
    "ChosenAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex")
);
