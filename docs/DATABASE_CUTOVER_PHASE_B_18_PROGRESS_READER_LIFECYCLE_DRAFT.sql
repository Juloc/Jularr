-- Isolated Phase B target contract; never execute against an active database.
BEGIN;

ALTER TABLE "WorkEpisodes" ADD COLUMN "IsSpecial" boolean NOT NULL DEFAULT false;
CREATE INDEX "IX_WorkEpisodes_RegularWorkOrder" ON "WorkEpisodes"
    ("WorkId", "WorkSeasonId", "EpisodeNumber", "OrderIndex", "Id") WHERE NOT "IsSpecial";

CREATE TABLE "ProgressCompletionReasonTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ProgressCompletionReasonTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ProgressCompletionReasonTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ProgressCompletionReasonTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ProgressCompletionReasonTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ProgressCompletionReasonTypes" ("Id", "Key")
VALUES
    (1, 'playback_ended'),
    (2, 'reader_finished'),
    (3, 'user_marked');

CREATE TABLE "ProgressCheckpointSourceTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ProgressCheckpointSourceTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ProgressCheckpointSourceTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ProgressCheckpointSourceTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ProgressCheckpointSourceTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ProgressCheckpointSourceTypes" ("Id", "Key")
VALUES
    (1, 'live_client'),
    (2, 'offline_client'),
    (3, 'user_action');

ALTER TABLE "MediaProgress"
    ADD COLUMN "CompletionReasonTypeId" smallint,
    ADD COLUMN "CompletedAt" timestamptz,
    ADD COLUMN "CompletedByAccountId" bigint,
    ADD CONSTRAINT "FK_MediaProgress_CompletionReasons" FOREIGN KEY ("CompletionReasonTypeId")
        REFERENCES "ProgressCompletionReasonTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_MediaProgress_CompletedByAccount" FOREIGN KEY ("CompletedByAccountId")
        REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_MediaProgress_Completion" CHECK (
        (NOT "IsCompleted" AND num_nonnulls("CompletionReasonTypeId", "CompletedAt", "CompletedByAccountId") = 0)
        OR ("IsCompleted" AND num_nonnulls("CompletionReasonTypeId", "CompletedAt", "CompletedByAccountId") = 3
            AND "CompletedAt" >= "CreatedAt")),
    ADD CONSTRAINT "CK_MediaProgress_CompletionPosition" CHECK (
        "CompletionReasonTypeId" IS NULL OR "CompletionReasonTypeId" = 3
        OR ("CompletionReasonTypeId" = 1 AND "ProgressPositionTypeId" = 1)
        OR ("CompletionReasonTypeId" = 2 AND "ProgressPositionTypeId" = 2));

ALTER TABLE "MediaProgressCheckpointEvents"
    ADD COLUMN "ActorAccountId" bigint NOT NULL,
    ADD COLUMN "ProgressCheckpointSourceTypeId" smallint NOT NULL,
    ADD COLUMN "PayloadHash" bytea NOT NULL,
    ADD COLUMN "CompletionReasonTypeId" smallint,
    ADD COLUMN "ClearsCompletion" boolean NOT NULL DEFAULT false,
    ADD COLUMN "CompletionPositionTypeId" smallint GENERATED ALWAYS AS (
        CASE "CompletionReasonTypeId" WHEN 1 THEN 1::smallint WHEN 2 THEN 2::smallint ELSE NULL::smallint END) STORED,
    ADD CONSTRAINT "FK_MediaProgressCheckpointEvents_Actor" FOREIGN KEY ("ActorAccountId")
        REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_MediaProgressCheckpointEvents_Source" FOREIGN KEY ("ProgressCheckpointSourceTypeId")
        REFERENCES "ProgressCheckpointSourceTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_MediaProgressCheckpointEvents_CompletionReasons" FOREIGN KEY ("CompletionReasonTypeId")
        REFERENCES "ProgressCompletionReasonTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_MediaProgressCheckpointEvents_CompletionPosition" FOREIGN KEY ("MediaProgressId", "CompletionPositionTypeId")
        REFERENCES "MediaProgress" ("Id", "ProgressPositionTypeId") ON DELETE RESTRICT,
    ADD CONSTRAINT "UX_MediaProgressCheckpointEvents_Progress_Revision" UNIQUE ("MediaProgressId", "AcceptedRevision"),
    ADD CONSTRAINT "CK_MediaProgressCheckpointEvents_Hash" CHECK (octet_length("PayloadHash") = 32),
    ADD CONSTRAINT "CK_MediaProgressCheckpointEvents_Completion" CHECK (
        (NOT "ClearsCompletion" OR ("ProgressCheckpointSourceTypeId" = 3 AND "CompletionReasonTypeId" IS NULL))
        AND ("ProgressCheckpointSourceTypeId" <> 3 OR "CompletionReasonTypeId" IS NULL OR "CompletionReasonTypeId" = 3)
        AND ("CompletionReasonTypeId" IS DISTINCT FROM 3 OR "ProgressCheckpointSourceTypeId" = 3));

ALTER TABLE "ReaderContent"
    ADD COLUMN "WorkChapterId" bigint,
    ADD CONSTRAINT "FK_ReaderContent_WorkChapters" FOREIGN KEY ("WorkChapterId", "WorkId")
        REFERENCES "WorkChapters" ("Id", "WorkId") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_ReaderContent_StorageKey" CHECK (
        length("StorageKey") BETWEEN 1 AND 1024 AND "StorageKey" = btrim("StorageKey"));

ALTER TABLE "ReadingProgressPositions"
    ADD CONSTRAINT "FK_ReadingProgressPositions_Page" FOREIGN KEY ("ReaderContentId", "PageIndex")
        REFERENCES "ReaderPages" ("ReaderContentId", "PageIndex") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_ReadingProgressPositions_Anchor" CHECK (
        ("ReaderContentId" IS NULL AND num_nonnulls("PageIndex", "TextLocator", "ProgressPermille") = 0)
        OR ("ReaderContentId" IS NOT NULL AND num_nonnulls("PageIndex", "TextLocator") = 1)),
    ADD CONSTRAINT "CK_ReadingProgressPositions_Locator" CHECK (
        "TextLocator" IS NULL
        OR (jsonb_typeof("TextLocator") = 'object' AND octet_length("TextLocator"::text) <= 16384));

CREATE INDEX "IX_ReadingProgressPositions_Content_Page" ON "ReadingProgressPositions"
    ("ReaderContentId", "PageIndex") WHERE "ReaderContentId" IS NOT NULL;
CREATE INDEX "IX_ReaderContent_Chapter" ON "ReaderContent" ("WorkChapterId") WHERE "WorkChapterId" IS NOT NULL;

CREATE FUNCTION "RejectReaderContentMutation"() RETURNS trigger
LANGUAGE plpgsql AS $reader_content$
BEGIN
    IF NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Reader content revisions are immutable; publish a new revision'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_ImmutableRevision';
    END IF;
    RETURN NEW;
END $reader_content$;
CREATE TRIGGER "TR_ReaderContent_ImmutableRevision"
    BEFORE UPDATE ON "ReaderContent"
    FOR EACH ROW EXECUTE FUNCTION "RejectReaderContentMutation"();

CREATE FUNCTION "ValidateReadingProgressTarget"() RETURNS trigger
LANGUAGE plpgsql AS $reading_target$
DECLARE
    invalid_target boolean;
    progress_id bigint;
BEGIN
    IF TG_TABLE_NAME = 'MediaProgress' THEN
        progress_id := NEW."Id";
    ELSE
        progress_id := NEW."MediaProgressId";
    END IF;
    SELECT EXISTS (
        SELECT 1
        FROM "MediaProgress" AS progress
        INNER JOIN "ReadingProgressPositions" AS position ON position."MediaProgressId" = progress."Id"
        INNER JOIN "ReaderContent" AS content ON content."Id" = position."ReaderContentId"
        WHERE progress."Id" = progress_id
            AND ((progress."WorkEditionId" IS NOT NULL AND progress."WorkEditionId" <> content."WorkEditionId")
                OR progress."WorkChapterId" IS DISTINCT FROM content."WorkChapterId")
    ) INTO invalid_target;
    IF invalid_target THEN
        RAISE EXCEPTION 'Reader anchor does not match the exact progress target'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReadingProgressPositions_ExactTarget';
    END IF;
    RETURN NULL;
END $reading_target$;
CREATE CONSTRAINT TRIGGER "TR_ReadingProgressPositions_ExactTarget"
    AFTER INSERT OR UPDATE ON "ReadingProgressPositions"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateReadingProgressTarget"();
CREATE CONSTRAINT TRIGGER "TR_MediaProgress_ReaderTarget"
    AFTER INSERT OR UPDATE ON "MediaProgress"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateReadingProgressTarget"();

CREATE FUNCTION "RejectProgressReceiptMutation"() RETURNS trigger
LANGUAGE plpgsql AS $progress_receipt$
BEGIN
    IF NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Accepted checkpoint receipts are immutable'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_MediaProgressCheckpointEvents_Immutable';
    END IF;
    RETURN NEW;
END $progress_receipt$;
CREATE TRIGGER "TR_MediaProgressCheckpointEvents_Immutable"
    BEFORE UPDATE ON "MediaProgressCheckpointEvents"
    FOR EACH ROW EXECUTE FUNCTION "RejectProgressReceiptMutation"();

COMMIT;
