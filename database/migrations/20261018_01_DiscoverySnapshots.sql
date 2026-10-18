-- Version 2: shared provider-owned discovery snapshots.
-- DatabaseBaseline owns the transaction and the version ledger.
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
