BEGIN;

ALTER TABLE "Providers"
    ADD CONSTRAINT "UX_Providers_Id_Key" UNIQUE ("Id", "Key");

CREATE TABLE "PlexSettings" (
    "ProviderId" bigint NOT NULL,
    "ProviderKey" text GENERATED ALWAYS AS ('plex') STORED,
    "RequireApproval" boolean NOT NULL DEFAULT true,
    "ClientIdentifier" varchar(200) NOT NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_PlexSettings" PRIMARY KEY ("ProviderId"),
    CONSTRAINT "FK_PlexSettings_ProviderKey" FOREIGN KEY ("ProviderId", "ProviderKey") REFERENCES "Providers" ("Id", "Key") ON DELETE RESTRICT,
    CONSTRAINT "CK_PlexSettings_Client" CHECK (length("ClientIdentifier") > 0 AND "ClientIdentifier" = btrim("ClientIdentifier"))
);

ALTER TABLE "ProviderMediaConnections"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "RevokedAt" timestamptz,
    ADD CONSTRAINT "UX_ProviderMediaConnections_PublicId" UNIQUE ("PublicId"),
    ADD CONSTRAINT "CK_ProviderMediaConnections_Revision" CHECK ("Revision" >= 1),
    ADD CONSTRAINT "CK_ProviderMediaConnections_Revoked" CHECK ("RevokedAt" IS NULL OR "RevokedAt" >= "ConsentedAt"),
    ADD CONSTRAINT "CK_ProviderMediaConnections_OpaqueCredential" CHECK ("CredentialsStorageKey" ~ '^[A-Za-z0-9_-]{16,200}$');

ALTER TABLE "ProviderServerGrants"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "ApprovedEndpoint" varchar(2048) NOT NULL,
    ADD COLUMN "VerifiedOwnerExternalAccountId" varchar(200) NOT NULL,
    ADD COLUMN "RevokedAt" timestamptz,
    ADD CONSTRAINT "UX_ProviderServerGrants_PublicId" UNIQUE ("PublicId"),
    ADD CONSTRAINT "CK_ProviderServerGrants_Revision" CHECK ("Revision" >= 1),
    ADD CONSTRAINT "CK_ProviderServerGrants_Revoked" CHECK ("RevokedAt" IS NULL OR "RevokedAt" >= "ApprovedAt"),
    ADD CONSTRAINT "CK_ProviderServerGrants_Endpoint" CHECK ("ApprovedEndpoint" ~ '^https://[^/?#@[:space:]]+(/[^?#[:space:]]*)?$'),
    ADD CONSTRAINT "CK_ProviderServerGrants_Owner" CHECK (
        length("VerifiedOwnerExternalAccountId") > 0 AND "VerifiedOwnerExternalAccountId" = btrim("VerifiedOwnerExternalAccountId")),
    ADD CONSTRAINT "CK_ProviderServerGrants_OpaqueCredential" CHECK ("CredentialsStorageKey" ~ '^[A-Za-z0-9_-]{16,200}$');
CREATE INDEX "IX_ProviderServerGrants_Approver" ON "ProviderServerGrants" ("ApprovedByAccountId", "Id");

ALTER TABLE "ProviderCatalogCheckpoints"
    DROP COLUMN "GenerationId",
    DROP CONSTRAINT "CK_ProviderCatalogCheckpoints_PageSize",
    ADD COLUMN "Generation" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "GrantRevision" bigint NOT NULL,
    ADD COLUMN "OperationId" bigint NOT NULL,
    ADD COLUMN "RequestedByAccountId" bigint NOT NULL,
    ADD COLUMN "TotalSize" bigint NOT NULL DEFAULT 0,
    ADD COLUMN "LastPageStart" integer,
    ADD COLUMN "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN "CompletedAt" timestamptz,
    ADD CONSTRAINT "UX_ProviderCatalogCheckpoints_Generation" UNIQUE ("ProviderServerGrantId", "ExternalSectionId", "Generation"),
    ADD CONSTRAINT "FK_ProviderCatalogCheckpoints_Operations" FOREIGN KEY ("OperationId") REFERENCES "Operations" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ProviderCatalogCheckpoints_Accounts" FOREIGN KEY ("RequestedByAccountId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_ProviderCatalogCheckpoints_PageSize" CHECK ("PageSize" BETWEEN 1 AND 200),
    ADD CONSTRAINT "CK_ProviderCatalogCheckpoints_Revisions" CHECK ("Generation" >= 1 AND "GrantRevision" >= 1),
    ADD CONSTRAINT "CK_ProviderCatalogCheckpoints_Total" CHECK ("TotalSize" >= 0),
    ADD CONSTRAINT "CK_ProviderCatalogCheckpoints_LastPage" CHECK (
        ("PagesScanned" = 0 AND "LastPageStart" IS NULL AND "NextStart" = 0)
        OR ("PagesScanned" > 0 AND "LastPageStart" IS NOT NULL AND "LastPageStart" >= 0
            AND ("NextStart" IS NULL OR "NextStart" > "LastPageStart"))),
    ADD CONSTRAINT "CK_ProviderCatalogCheckpoints_Complete" CHECK (
        ("NextStart" IS NULL) = ("CompletedAt" IS NOT NULL)
        AND ("CompletedAt" IS NULL OR "CompletedAt" >= "CreatedAt"));
CREATE INDEX "IX_ProviderCatalogCheckpoints_Operation" ON "ProviderCatalogCheckpoints" ("OperationId");
CREATE INDEX "IX_ProviderCatalogCheckpoints_Requester" ON "ProviderCatalogCheckpoints" ("RequestedByAccountId");

ALTER TABLE "WorkExternalIdentities" ADD COLUMN "ConfirmedAt" timestamptz;

CREATE TABLE "ProviderCatalogItems" (
    "ProviderServerGrantId" bigint NOT NULL,
    "ExternalItemKey" varchar(200) NOT NULL,
    "ExternalSectionId" text NOT NULL,
    "Generation" bigint NOT NULL,
    "WorkId" bigint NOT NULL,
    "WorkEpisodeId" bigint,
    "SourcePageStart" integer NOT NULL,
    "VerifiedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_ProviderCatalogItems" PRIMARY KEY ("ProviderServerGrantId", "ExternalItemKey"),
    CONSTRAINT "FK_ProviderCatalogItems_Checkpoint" FOREIGN KEY ("ProviderServerGrantId", "ExternalSectionId", "Generation")
        REFERENCES "ProviderCatalogCheckpoints" ("ProviderServerGrantId", "ExternalSectionId", "Generation") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProviderCatalogItems_Works" FOREIGN KEY ("WorkId") REFERENCES "Works" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProviderCatalogItems_Episodes" FOREIGN KEY ("WorkEpisodeId", "WorkId") REFERENCES "WorkEpisodes" ("Id", "WorkId") ON DELETE RESTRICT,
    CONSTRAINT "CK_ProviderCatalogItems_Key" CHECK (length("ExternalItemKey") > 0 AND "ExternalItemKey" = btrim("ExternalItemKey")),
    CONSTRAINT "CK_ProviderCatalogItems_Start" CHECK ("SourcePageStart" >= 0)
);
CREATE INDEX "IX_ProviderCatalogItems_Work" ON "ProviderCatalogItems" ("WorkId", "VerifiedAt" DESC, "ProviderServerGrantId", "ExternalItemKey");
CREATE INDEX "IX_ProviderCatalogItems_Checkpoint" ON "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalSectionId", "Generation", "SourcePageStart" DESC);

CREATE FUNCTION "ValidateProviderCatalogPage"() RETURNS trigger
LANGUAGE plpgsql AS $provider_catalog_page$
DECLARE
    accepted_start integer;
    source_start integer;
    current_generation bigint;
BEGIN
    IF TG_TABLE_NAME = 'ProviderCatalogItems' THEN
        SELECT
            checkpoint."LastPageStart",
            item."SourcePageStart"
        INTO accepted_start, source_start
        FROM
            "ProviderCatalogItems" AS item
        INNER JOIN
            "ProviderCatalogCheckpoints" AS checkpoint ON checkpoint."ProviderServerGrantId" = item."ProviderServerGrantId"
                AND checkpoint."ExternalSectionId" = item."ExternalSectionId" AND checkpoint."Generation" = item."Generation"
        WHERE
            item."ProviderServerGrantId" = NEW."ProviderServerGrantId"
            AND item."ExternalItemKey" = NEW."ExternalItemKey";
    ELSE
        SELECT
            checkpoint."LastPageStart",
            checkpoint."Generation"
        INTO accepted_start, current_generation
        FROM
            "ProviderCatalogCheckpoints" AS checkpoint
        WHERE
            checkpoint."ProviderServerGrantId" = NEW."ProviderServerGrantId"
            AND checkpoint."ExternalSectionId" = NEW."ExternalSectionId";
        SELECT
            item."SourcePageStart"
        INTO source_start
        FROM
            "ProviderCatalogItems" AS item
        WHERE
            item."ProviderServerGrantId" = NEW."ProviderServerGrantId"
            AND item."ExternalSectionId" = NEW."ExternalSectionId"
            AND item."Generation" = current_generation
        ORDER BY
            item."SourcePageStart" DESC
        LIMIT 1;
    END IF;
    IF source_start IS NOT NULL AND (accepted_start IS NULL OR source_start > accepted_start) THEN
        RAISE EXCEPTION 'Catalog items require an accepted source page'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ProviderCatalogItems_AcceptedPage';
    END IF;
    RETURN NULL;
END $provider_catalog_page$;
CREATE CONSTRAINT TRIGGER "TR_ProviderCatalogItems_AcceptedPage"
AFTER INSERT OR UPDATE ON "ProviderCatalogItems"
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateProviderCatalogPage"();
CREATE CONSTRAINT TRIGGER "TR_ProviderCatalogCheckpoints_AcceptedPage"
AFTER UPDATE ON "ProviderCatalogCheckpoints"
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateProviderCatalogPage"();

COMMIT;
