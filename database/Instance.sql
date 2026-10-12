BEGIN;

CREATE TABLE "InstanceSettings" (
    "Id" bigint PRIMARY KEY CHECK ("Id" = 1),
    "Name" varchar(128) NOT NULL CHECK (length(btrim("Name")) > 0),
    "UiLocaleId" bigint NOT NULL REFERENCES "UiLocales" ("Id") ON DELETE RESTRICT,
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version" > 0)
);

-- Use the shared English fallback.
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('en', 'English')
ON CONFLICT ("Locale") DO NOTHING;

INSERT INTO "InstanceSettings" ("Id", "Name", "UiLocaleId")
SELECT 1, 'Jularr', "Id" FROM "UiLocales" WHERE "Locale" = 'en';

COMMIT;
