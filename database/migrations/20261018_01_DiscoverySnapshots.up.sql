BEGIN;

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

-- Versioned, manual upgrade for existing installations. Fresh installations
-- execute this same script only during the empty-database bootstrap.
CREATE TABLE IF NOT EXISTS "SchemaMigrations" (
    "Key" varchar(128) PRIMARY KEY,
    "AppliedAt" timestamptz NOT NULL DEFAULT now()
);

DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM "SchemaMigrations"
        WHERE "Key" = '20261018_01_DiscoverySnapshots'
    ) THEN
        RAISE EXCEPTION 'Migration 20261018_01_DiscoverySnapshots is already installed';
    END IF;
END
$migration$;

-- Only source-owned public browse results belong here. Never store profile
-- progress, private searches or permanent Works in this cache.
CREATE TABLE "DiscoverySnapshots" (
    "Key" varchar(512) PRIMARY KEY,
    "ProviderKey" varchar(64) NOT NULL CHECK (length(btrim("ProviderKey")) > 0),
    "Payload" jsonb NOT NULL CHECK (jsonb_typeof("Payload") = 'array' AND octet_length("Payload"::text) <= 2097152),
    "FetchedAt" timestamptz NOT NULL,
    "FreshUntil" timestamptz NOT NULL,
    "RetainUntil" timestamptz NOT NULL,
    CONSTRAINT "CK_DiscoverySnapshots_Dates" CHECK (
        "FreshUntil" >= "FetchedAt"
        AND "RetainUntil" >= "FreshUntil"
        AND "RetainUntil" > "FetchedAt"
    )
);
CREATE INDEX "IX_DiscoverySnapshots_RetainUntil" ON "DiscoverySnapshots" ("RetainUntil");
CREATE INDEX "IX_DiscoverySnapshots_ProviderKey" ON "DiscoverySnapshots" ("ProviderKey");

INSERT INTO "SchemaMigrations" ("Key")
VALUES ('20261018_01_DiscoverySnapshots');

COMMIT;
