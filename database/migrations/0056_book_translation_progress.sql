-- How far the book translation is, counted when a translation is written or deleted instead of on every look at the progress: per book and language the
-- sections and blocks, and those translated completely. ModelKey and Digest are empty for sections complete in any model and version; other rows hold
-- one model's cache key and the version (digest) its complete sections have. The bookTranslations job counts every book again after a start.
CREATE TABLE IF NOT EXISTS "BookTranslationProgress" (
    "ReadingDocumentId" bigint NOT NULL REFERENCES "ReadingDocuments" ("Id") ON DELETE CASCADE,
    "Language" varchar(3) NOT NULL CHECK ("Language" ~ '^[a-z]{2,3}$'),
    "ModelKey" varchar(80) NOT NULL,
    "Digest" varchar(80) NOT NULL,
    "Sections" integer NOT NULL CHECK ("Sections" >= 0),
    "DoneSections" integer NOT NULL CHECK ("DoneSections" >= 0),
    "Blocks" integer NOT NULL CHECK ("Blocks" >= 0),
    "DoneBlocks" integer NOT NULL CHECK ("DoneBlocks" >= 0),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("ReadingDocumentId", "Language", "ModelKey", "Digest"),
    CHECK (("ModelKey" = '') = ("Digest" = ''))
);
