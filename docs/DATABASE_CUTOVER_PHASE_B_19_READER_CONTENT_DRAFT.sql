-- Isolated target contract, not a runtime migration.
BEGIN;

CREATE TABLE "ReaderLayoutTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderLayoutTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderLayoutTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderLayoutTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderLayoutTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderLayoutTypes" ("Id", "Key")
VALUES
    (1, 'reflowable_text'),
    (2, 'image_sequence'),
    (3, 'fixed_pages');

CREATE TABLE "ReaderVariantTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderVariantTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderVariantTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderVariantTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderVariantTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderVariantTypes" ("Id", "Key")
VALUES
    (1, 'original'),
    (2, 'official_translation'),
    (3, 'generated_translation');

CREATE TABLE "ReaderNodeTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderNodeTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderNodeTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderNodeTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderNodeTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderNodeTypes" ("Id", "Key")
VALUES
    (1, 'text'),
    (2, 'image'),
    (3, 'scene_break'),
    (4, 'fixed_region');

CREATE TABLE "ReaderTextBlockTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderTextBlockTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderTextBlockTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderTextBlockTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderTextBlockTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderTextBlockTypes" ("Id", "Key")
VALUES
    (1, 'paragraph'),
    (2, 'heading'),
    (3, 'quote'),
    (4, 'preformatted'),
    (5, 'list'),
    (6, 'table'),
    (7, 'footnote'),
    (8, 'formula');

CREATE TABLE "ReaderPageDirectionTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderPageDirectionTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderPageDirectionTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderPageDirectionTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderPageDirectionTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderPageDirectionTypes" ("Id", "Key")
VALUES
    (1, 'auto'),
    (2, 'left_to_right'),
    (3, 'right_to_left');

CREATE TABLE "ReaderTextDirectionTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderTextDirectionTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderTextDirectionTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderTextDirectionTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderTextDirectionTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderTextDirectionTypes" ("Id", "Key")
VALUES
    (1, 'auto'),
    (2, 'left_to_right'),
    (3, 'right_to_left');

CREATE TABLE "ReaderWritingModeTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_ReaderWritingModeTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderWritingModeTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_ReaderWritingModeTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_ReaderWritingModeTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "ReaderWritingModeTypes" ("Id", "Key")
VALUES
    (1, 'horizontal_tb'),
    (2, 'vertical_rl'),
    (3, 'vertical_lr');

ALTER TABLE "WorkVersions" ADD CONSTRAINT "UX_WorkVersions_Id_Edition_Work"
    UNIQUE ("Id", "WorkEditionId", "WorkId");
ALTER TABLE "ReaderContent"
    DROP CONSTRAINT "UX_ReaderContent_Edition_Revision",
    ADD COLUMN "VariantKey" varchar(100) NOT NULL DEFAULT 'original',
    ADD COLUMN "ReaderVariantTypeId" smallint NOT NULL DEFAULT 1,
    ADD COLUMN "ReaderLayoutTypeId" smallint NOT NULL,
    ADD COLUMN "LanguageTag" varchar(32) NOT NULL DEFAULT 'und',
    ADD COLUMN "PageDirectionTypeId" smallint NOT NULL DEFAULT 1,
    ADD COLUMN "TextDirectionTypeId" smallint NOT NULL DEFAULT 1,
    ADD COLUMN "WritingModeTypeId" smallint NOT NULL DEFAULT 1,
    ADD COLUMN "HasTextLayer" boolean NOT NULL DEFAULT FALSE,
    ADD COLUMN "SourceReaderContentId" bigint,
    ADD COLUMN "SourceWorkVersionId" bigint,
    ADD COLUMN "SourceStoredFileId" bigint,
    ADD COLUMN "ContentHash" bytea NOT NULL,
    ADD COLUMN "ParserVersion" varchar(100) NOT NULL,
    ADD COLUMN "BuilderOperationId" bigint NOT NULL,
    ADD COLUMN "NodeCount" integer NOT NULL,
    ADD COLUMN "PageCount" integer NOT NULL,
    ADD COLUMN "SourcePageCount" integer,
    ADD COLUMN "PublishedAt" timestamptz,
    ADD CONSTRAINT "UX_ReaderContent_Edition_Chapter_Variant_Revision" UNIQUE NULLS NOT DISTINCT
        ("WorkEditionId", "WorkChapterId", "VariantKey", "ContentRevision"),
    ADD CONSTRAINT "UX_ReaderContent_Id_Source" UNIQUE ("Id", "SourceReaderContentId"),
    ADD CONSTRAINT "FK_ReaderContent_Layout" FOREIGN KEY ("ReaderLayoutTypeId") REFERENCES "ReaderLayoutTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_Variant" FOREIGN KEY ("ReaderVariantTypeId") REFERENCES "ReaderVariantTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_PageDirection" FOREIGN KEY ("PageDirectionTypeId") REFERENCES "ReaderPageDirectionTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_TextDirection" FOREIGN KEY ("TextDirectionTypeId") REFERENCES "ReaderTextDirectionTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_WritingMode" FOREIGN KEY ("WritingModeTypeId") REFERENCES "ReaderWritingModeTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_Source" FOREIGN KEY ("SourceReaderContentId", "WorkId") REFERENCES "ReaderContent" ("Id", "WorkId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_Version" FOREIGN KEY ("SourceWorkVersionId", "WorkEditionId", "WorkId") REFERENCES "WorkVersions" ("Id", "WorkEditionId", "WorkId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_File" FOREIGN KEY ("SourceStoredFileId", "SourceWorkVersionId") REFERENCES "StoredFiles" ("Id", "WorkVersionId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_ReaderContent_Operation" FOREIGN KEY ("BuilderOperationId") REFERENCES "Operations" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_ReaderContent_VariantKey" CHECK ("VariantKey" ~ '^[a-z0-9][a-z0-9:_-]{0,99}$'),
    ADD CONSTRAINT "CK_ReaderContent_Language" CHECK ("LanguageTag" ~ '^[a-z]{1,8}(-[a-z0-9]{1,8})*$'),
    ADD CONSTRAINT "CK_ReaderContent_Hash" CHECK (octet_length("ContentHash") = 32),
    ADD CONSTRAINT "CK_ReaderContent_OpaqueStorage" CHECK ("StorageKey" ~ '^[A-Za-z0-9_-]{1,200}$'),
    ADD CONSTRAINT "CK_ReaderContent_Parser" CHECK (length("ParserVersion") > 0 AND "ParserVersion" = btrim("ParserVersion")),
    ADD CONSTRAINT "CK_ReaderContent_Source" CHECK (
        "SourceReaderContentId" IS DISTINCT FROM "Id"
        AND ("ReaderVariantTypeId" <> 3 OR "SourceReaderContentId" IS NOT NULL)
        AND num_nonnulls("SourceWorkVersionId", "SourceStoredFileId") IN (0, 2)),
    ADD CONSTRAINT "CK_ReaderContent_Counts" CHECK (
        "NodeCount" >= 0 AND "PageCount" >= 0
        AND (("ReaderLayoutTypeId" = 1 AND "NodeCount" > 0 AND "PageCount" = 0)
            OR ("ReaderLayoutTypeId" IN (2, 3) AND "PageCount" > 0))
        AND ("SourcePageCount" IS NULL OR "SourcePageCount" > 0)
        AND ("ReaderLayoutTypeId" <> 3 OR "SourcePageCount" IS NOT NULL)),
    ADD CONSTRAINT "CK_ReaderContent_Publication" CHECK ("PublishedAt" IS NULL OR "PublishedAt" >= "CreatedAt");
CREATE INDEX "IX_ReaderContent_Source" ON "ReaderContent" ("SourceReaderContentId") WHERE "SourceReaderContentId" IS NOT NULL;
CREATE INDEX "IX_ReaderContent_Operation" ON "ReaderContent" ("BuilderOperationId");

ALTER TABLE "ReaderPages"
    ADD COLUMN "SourcePageIndex" integer,
    ADD CONSTRAINT "CK_ReaderPages_SourceIndex" CHECK ("SourcePageIndex" IS NULL OR "SourcePageIndex" >= 0);

CREATE FUNCTION "IsReaderRegionValid"(region jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE STRICT AS $region$
BEGIN
    IF jsonb_typeof(region) <> 'object' OR NOT region ?& ARRAY['x', 'y', 'width', 'height']
        OR region - ARRAY['x', 'y', 'width', 'height'] <> '{}'::jsonb
        OR jsonb_typeof(region->'x') <> 'number' OR jsonb_typeof(region->'y') <> 'number'
        OR jsonb_typeof(region->'width') <> 'number' OR jsonb_typeof(region->'height') <> 'number' THEN
        RETURN FALSE;
    END IF;
    RETURN (region->>'x')::numeric BETWEEN 0 AND 1 AND (region->>'y')::numeric BETWEEN 0 AND 1
        AND (region->>'width')::numeric > 0 AND (region->>'height')::numeric > 0
        AND (region->>'x')::numeric + (region->>'width')::numeric <= 1
        AND (region->>'y')::numeric + (region->>'height')::numeric <= 1;
END $region$;

CREATE FUNCTION "IsReaderViewportValid"(viewport jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE STRICT AS $viewport$
BEGIN
    IF jsonb_typeof(viewport) <> 'object' OR NOT viewport ?& ARRAY['x', 'y', 'zoom']
        OR viewport - ARRAY['x', 'y', 'zoom'] <> '{}'::jsonb
        OR jsonb_typeof(viewport->'x') <> 'number' OR jsonb_typeof(viewport->'y') <> 'number'
        OR jsonb_typeof(viewport->'zoom') <> 'number' THEN
        RETURN FALSE;
    END IF;
    RETURN (viewport->>'x')::numeric BETWEEN 0 AND 1 AND (viewport->>'y')::numeric BETWEEN 0 AND 1
        AND (viewport->>'zoom')::numeric > 0 AND (viewport->>'zoom')::numeric <= 1.7976931348623157e308;
END $viewport$;

CREATE TABLE "ReaderContentNodes" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ReaderContentId" bigint NOT NULL,
    "StableId" varchar(200) NOT NULL,
    "OrderIndex" integer NOT NULL,
    "ReaderNodeTypeId" smallint NOT NULL,
    "ReaderTextBlockTypeId" smallint,
    "CharacterCount" integer,
    "HeadingLevel" smallint NOT NULL DEFAULT 0,
    "SourceReaderContentId" bigint,
    "SourceNodeId" bigint,
    "SourcePageIndex" integer,
    "SourceRegion" jsonb,
    CONSTRAINT "PK_ReaderContentNodes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_ReaderContentNodes_Content_StableId" UNIQUE ("ReaderContentId", "StableId"),
    CONSTRAINT "UX_ReaderContentNodes_Content_Order" UNIQUE ("ReaderContentId", "OrderIndex"),
    CONSTRAINT "UX_ReaderContentNodes_Id_Content" UNIQUE ("Id", "ReaderContentId"),
    CONSTRAINT "FK_ReaderContentNodes_Content" FOREIGN KEY ("ReaderContentId") REFERENCES "ReaderContent" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ReaderContentNodes_SourceContent" FOREIGN KEY ("ReaderContentId", "SourceReaderContentId")
        REFERENCES "ReaderContent" ("Id", "SourceReaderContentId") ON DELETE RESTRICT,
    CONSTRAINT "FK_ReaderContentNodes_SourceNode" FOREIGN KEY ("SourceNodeId", "SourceReaderContentId")
        REFERENCES "ReaderContentNodes" ("Id", "ReaderContentId") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT "FK_ReaderContentNodes_NodeType" FOREIGN KEY ("ReaderNodeTypeId") REFERENCES "ReaderNodeTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ReaderContentNodes_BlockType" FOREIGN KEY ("ReaderTextBlockTypeId") REFERENCES "ReaderTextBlockTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_ReaderContentNodes_Identity" CHECK (
        length("StableId") > 0 AND "StableId" = btrim("StableId") AND "OrderIndex" >= 0),
    CONSTRAINT "CK_ReaderContentNodes_Text" CHECK (
        ("ReaderNodeTypeId" = 1 AND "ReaderTextBlockTypeId" IS NOT NULL AND "CharacterCount" >= 0
            AND "CharacterCount" IS NOT NULL
            AND (("ReaderTextBlockTypeId" = 2 AND "HeadingLevel" BETWEEN 1 AND 6)
                OR ("ReaderTextBlockTypeId" <> 2 AND "HeadingLevel" = 0)))
        OR ("ReaderNodeTypeId" <> 1 AND num_nonnulls("ReaderTextBlockTypeId", "CharacterCount") = 0 AND "HeadingLevel" = 0)),
    CONSTRAINT "CK_ReaderContentNodes_Source" CHECK (
        ("SourceNodeId" IS NULL OR "SourceReaderContentId" IS NOT NULL)
        AND num_nonnulls("SourceNodeId", "SourcePageIndex") <= 1
        AND ("SourcePageIndex" IS NULL OR "SourcePageIndex" >= 0)
        AND ("SourceRegion" IS NULL OR ("SourcePageIndex" IS NOT NULL AND "IsReaderRegionValid"("SourceRegion")))
        AND ("ReaderNodeTypeId" <> 4 OR "SourcePageIndex" IS NOT NULL))
);
CREATE INDEX "IX_ReaderContentNodes_SourceNode" ON "ReaderContentNodes" ("SourceNodeId", "SourceReaderContentId") WHERE "SourceNodeId" IS NOT NULL;

CREATE OR REPLACE FUNCTION "RejectReaderContentMutation"() RETURNS trigger
LANGUAGE plpgsql AS $reader_content$
BEGIN
    IF (to_jsonb(NEW) - 'PublishedAt') IS DISTINCT FROM (to_jsonb(OLD) - 'PublishedAt')
        OR (OLD."PublishedAt" IS NOT NULL AND NEW."PublishedAt" IS DISTINCT FROM OLD."PublishedAt") THEN
        RAISE EXCEPTION 'Reader revision metadata and published content are immutable'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_ImmutableRevision';
    END IF;
    RETURN NEW;
END $reader_content$;

CREATE FUNCTION "GuardReaderSource"() RETURNS trigger
LANGUAGE plpgsql AS $reader_source$
DECLARE
    source_row record;
BEGIN
    IF NEW."SourceReaderContentId" IS NOT NULL THEN
        SELECT
            content."PublishedAt",
            content."WorkId",
            content."WorkEditionId",
            content."WorkChapterId"
        INTO source_row
        FROM "ReaderContent" AS content
        WHERE content."Id" = NEW."SourceReaderContentId"
        FOR SHARE OF content;
        IF NOT FOUND OR source_row."PublishedAt" IS NULL THEN
            RAISE EXCEPTION 'Derived content requires an exact published source revision'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_PublishedSource';
        END IF;
        IF source_row."WorkId" = NEW."WorkId"
            AND (source_row."WorkEditionId" <> NEW."WorkEditionId"
                OR source_row."WorkChapterId" IS DISTINCT FROM NEW."WorkChapterId") THEN
            RAISE EXCEPTION 'Derived content must retain the exact edition and chapter target'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_SourceTarget';
        END IF;
    END IF;
    RETURN NEW;
END $reader_source$;
CREATE TRIGGER "TR_ReaderContent_Source" BEFORE INSERT ON "ReaderContent"
    FOR EACH ROW EXECUTE FUNCTION "GuardReaderSource"();

CREATE FUNCTION "GuardReaderIndexMutation"() RETURNS trigger
LANGUAGE plpgsql AS $reader_index$
DECLARE
    published_at timestamptz;
    content_id bigint;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW."ReaderContentId" IS DISTINCT FROM OLD."ReaderContentId" THEN
        RAISE EXCEPTION 'Reader index entries cannot move to another revision'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_ImmutableIndex';
    END IF;
    content_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."ReaderContentId" ELSE NEW."ReaderContentId" END;
    SELECT
        content."PublishedAt"
    INTO published_at
    FROM
        "ReaderContent" AS content
    WHERE
        content."Id" = content_id
    FOR SHARE OF content;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    IF published_at IS NOT NULL THEN
        RAISE EXCEPTION 'Published reader indexes are immutable'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_ImmutableIndex';
    END IF;
    RETURN NEW;
END $reader_index$;
CREATE TRIGGER "TR_ReaderPages_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON "ReaderPages"
    FOR EACH ROW EXECUTE FUNCTION "GuardReaderIndexMutation"();
CREATE TRIGGER "TR_ReaderContentNodes_Immutable" BEFORE INSERT OR UPDATE OR DELETE ON "ReaderContentNodes"
    FOR EACH ROW EXECUTE FUNCTION "GuardReaderIndexMutation"();

CREATE FUNCTION "ValidateReaderPublication"() RETURNS trigger
LANGUAGE plpgsql AS $reader_publication$
DECLARE
    content_id bigint;
    content_row record;
    actual_nodes bigint;
    actual_pages bigint;
BEGIN
    IF TG_TABLE_NAME = 'ReaderContent' THEN
        content_id := NEW."Id";
    ELSIF TG_OP = 'DELETE' THEN
        content_id := OLD."ReaderContentId";
    ELSE
        content_id := NEW."ReaderContentId";
    END IF;
    SELECT
        content."PublishedAt",
        content."NodeCount",
        content."PageCount",
        content."ReaderLayoutTypeId",
        content."SourcePageCount"
    INTO content_row
    FROM "ReaderContent" AS content
    WHERE content."Id" = content_id;
    IF NOT FOUND OR content_row."PublishedAt" IS NULL THEN RETURN NULL; END IF;
    SELECT
        count(node."Id")
    INTO actual_nodes
    FROM
        "ReaderContentNodes" AS node
    WHERE
        node."ReaderContentId" = content_id;
    SELECT
        count(page."Id")
    INTO actual_pages
    FROM
        "ReaderPages" AS page
    WHERE
        page."ReaderContentId" = content_id;
    IF actual_nodes <> content_row."NodeCount" OR actual_pages <> content_row."PageCount"
        OR EXISTS (
            SELECT
                1
            FROM
                "ReaderContentNodes" AS node
            WHERE node."ReaderContentId" = content_id AND node."OrderIndex" >= content_row."NodeCount")
        OR EXISTS (
            SELECT
                1
            FROM
                "ReaderPages" AS page
            WHERE page."ReaderContentId" = content_id AND page."PageIndex" >= content_row."PageCount"
                OR page."ReaderContentId" = content_id AND content_row."ReaderLayoutTypeId" = 3
                    AND (page."SourcePageIndex" IS NULL OR page."SourcePageIndex" >= content_row."SourcePageCount"))
        OR EXISTS (
            SELECT
                1
            FROM
                "ReaderContentNodes" AS node
            LEFT JOIN
                "ReaderContent" AS source ON source."Id" = node."SourceReaderContentId"
            WHERE
                node."ReaderContentId" = content_id AND node."SourcePageIndex" IS NOT NULL
                AND (COALESCE(source."SourcePageCount", source."PageCount", content_row."SourcePageCount", content_row."PageCount") = 0
                    OR node."SourcePageIndex" >= COALESCE(source."SourcePageCount", source."PageCount", content_row."SourcePageCount", content_row."PageCount"))) THEN
        RAISE EXCEPTION 'Reader publication requires a complete exact revision index'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderContent_PublishedIndex';
    END IF;
    RETURN NULL;
END $reader_publication$;
CREATE CONSTRAINT TRIGGER "TR_ReaderContent_PublishedIndex" AFTER INSERT OR UPDATE ON "ReaderContent"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateReaderPublication"();
CREATE CONSTRAINT TRIGGER "TR_ReaderPages_PublishedIndex" AFTER DELETE ON "ReaderPages"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateReaderPublication"();
CREATE CONSTRAINT TRIGGER "TR_ReaderContentNodes_PublishedIndex" AFTER DELETE ON "ReaderContentNodes"
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "ValidateReaderPublication"();

CREATE FUNCTION "IsReaderIntegerValid"(value jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE STRICT AS $integer$
    SELECT
        CASE WHEN jsonb_typeof(value) = 'number' THEN
            value::text ~ '^(0|[1-9][0-9]*)$' AND value::text::numeric <= 2147483647
        ELSE FALSE END;
$integer$;

CREATE FUNCTION "IsReaderLocatorValid"(content_id bigint, locator jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE STRICT AS $locator$
DECLARE
    layout_type smallint;
    character_count integer;
BEGIN
    IF jsonb_typeof(locator) <> 'object' OR octet_length(locator::text) > 16384 THEN RETURN FALSE; END IF;
    IF locator ? 'fallbackPercent' THEN
        IF jsonb_typeof(locator->'fallbackPercent') <> 'number' THEN RETURN FALSE; END IF;
        IF (locator->>'fallbackPercent')::numeric NOT BETWEEN 0 AND 100 THEN RETURN FALSE; END IF;
    END IF;
    IF locator ? 'viewport' AND NOT "IsReaderViewportValid"(locator->'viewport') THEN RETURN FALSE; END IF;
    IF locator ? 'sourceRegion' AND NOT "IsReaderRegionValid"(locator->'sourceRegion') THEN RETURN FALSE; END IF;
    SELECT
        content."ReaderLayoutTypeId"
    INTO layout_type
    FROM
        "ReaderContent" AS content
    WHERE
        content."Id" = content_id AND content."PublishedAt" IS NOT NULL;
    IF NOT FOUND THEN RETURN FALSE; END IF;
    IF layout_type = 1 THEN
        IF locator - ARRAY['blockId', 'characterOffset', 'fallbackPercent'] <> '{}'::jsonb
            OR NOT locator ?& ARRAY['blockId', 'characterOffset']
            OR jsonb_typeof(locator->'blockId') <> 'string'
            OR NOT "IsReaderIntegerValid"(locator->'characterOffset') THEN RETURN FALSE; END IF;
        SELECT
            node."CharacterCount"
        INTO character_count
        FROM
            "ReaderContentNodes" AS node
        WHERE
            node."ReaderContentId" = content_id AND node."StableId" = locator->>'blockId' AND node."ReaderNodeTypeId" = 1;
        RETURN FOUND AND (locator->>'characterOffset')::integer <= character_count;
    ELSIF layout_type = 2 THEN
        IF locator - ARRAY['pageIndex', 'viewport', 'fallbackPercent'] <> '{}'::jsonb
            OR NOT locator ? 'pageIndex' OR NOT "IsReaderIntegerValid"(locator->'pageIndex') THEN RETURN FALSE; END IF;
        RETURN EXISTS (
            SELECT
                1
            FROM
                "ReaderPages" AS page
            WHERE
                page."ReaderContentId" = content_id AND page."PageIndex" = (locator->>'pageIndex')::integer);
    ELSIF layout_type = 3 THEN
        IF locator - ARRAY['logicalPageIndex', 'sourcePageIndex', 'sourceRegion', 'viewport', 'fallbackPercent'] <> '{}'::jsonb
            OR NOT locator ?& ARRAY['logicalPageIndex', 'sourcePageIndex']
            OR NOT "IsReaderIntegerValid"(locator->'logicalPageIndex')
            OR NOT "IsReaderIntegerValid"(locator->'sourcePageIndex') THEN RETURN FALSE; END IF;
        RETURN EXISTS (
            SELECT
                1
            FROM
                "ReaderPages" AS page
            WHERE
                page."ReaderContentId" = content_id AND page."PageIndex" = (locator->>'logicalPageIndex')::integer
                AND page."SourcePageIndex" = (locator->>'sourcePageIndex')::integer);
    END IF;
    RETURN FALSE;
END $locator$;

ALTER TABLE "ReadingProgressPositions"
    DROP CONSTRAINT "FK_ReadingProgressPositions_Page",
    DROP CONSTRAINT "CK_ReadingProgressPositions_Anchor",
    DROP CONSTRAINT "CK_ReadingProgressPositions_Locator",
    DROP COLUMN "PageIndex";
ALTER TABLE "ReadingProgressPositions" RENAME COLUMN "TextLocator" TO "Locator";
ALTER TABLE "ReadingProgressPositions"
    ADD COLUMN "PageIndex" integer GENERATED ALWAYS AS (
        CASE
            WHEN "IsReaderIntegerValid"("Locator"->'pageIndex') THEN ("Locator"->>'pageIndex')::integer
            WHEN "IsReaderIntegerValid"("Locator"->'logicalPageIndex') THEN ("Locator"->>'logicalPageIndex')::integer
        END) STORED,
    ADD CONSTRAINT "FK_ReadingProgressPositions_Page" FOREIGN KEY ("ReaderContentId", "PageIndex")
        REFERENCES "ReaderPages" ("ReaderContentId", "PageIndex") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_ReadingProgressPositions_Anchor" CHECK (
        ("ReaderContentId" IS NULL AND num_nonnulls("Locator", "ProgressPermille") = 0)
        OR ("ReaderContentId" IS NOT NULL AND "Locator" IS NOT NULL));
CREATE INDEX "IX_ReadingProgressPositions_Content_Page" ON "ReadingProgressPositions"
    ("ReaderContentId", "PageIndex") WHERE "ReaderContentId" IS NOT NULL;
ALTER TABLE "ReaderBookmarks" RENAME COLUMN "TextLocator" TO "Locator";
ALTER TABLE "ReaderBookmarks"
    ADD COLUMN "Label" varchar(200),
    ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    ADD CONSTRAINT "CK_ReaderBookmarks_Revision" CHECK ("Revision" >= 1),
    ADD CONSTRAINT "CK_ReaderBookmarks_Time" CHECK ("UpdatedAt" >= "CreatedAt");
ALTER TABLE "ReaderHighlights"
    ADD COLUMN "SelectedText" text NOT NULL,
    ADD COLUMN "StartPosition" bigint NOT NULL,
    ADD COLUMN "EndPosition" bigint NOT NULL,
    ADD COLUMN "AnchorRange" int8range GENERATED ALWAYS AS (int8range("StartPosition", "EndPosition", '[)')) STORED,
    ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    ADD CONSTRAINT "CK_ReaderHighlights_Revision" CHECK ("Revision" >= 1),
    ADD CONSTRAINT "CK_ReaderHighlights_Time" CHECK ("UpdatedAt" >= "CreatedAt"),
    ADD CONSTRAINT "CK_ReaderHighlights_Note" CHECK ("Note" IS NULL OR length("Note") <= 2000),
    ADD CONSTRAINT "CK_ReaderHighlights_Text" CHECK (length("SelectedText") BETWEEN 1 AND 2000),
    ADD CONSTRAINT "CK_ReaderHighlights_Range" CHECK ("StartPosition" >= 0 AND "EndPosition" > "StartPosition");
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE "ReaderHighlights"
    ADD CONSTRAINT "UX_ReaderHighlights_NoOverlap" EXCLUDE USING gist (
        "ProfileId" WITH =, "ReaderContentId" WITH =, "AnchorRange" WITH &&);

CREATE FUNCTION "ValidateReaderAnchor"() RETURNS trigger
LANGUAGE plpgsql AS $reader_anchor$
DECLARE
    start_order integer;
    end_order integer;
BEGIN
    IF TG_OP = 'UPDATE' AND TG_TABLE_NAME IN ('ReaderBookmarks', 'ReaderHighlights') THEN
        IF (NEW."Id", NEW."PublicId", NEW."ProfileId", NEW."ReaderContentId", NEW."CreatedAt")
            IS DISTINCT FROM (OLD."Id", OLD."PublicId", OLD."ProfileId", OLD."ReaderContentId", OLD."CreatedAt")
            OR NEW."Revision" <> OLD."Revision" + 1 OR NEW."UpdatedAt" < OLD."UpdatedAt" THEN
            RAISE EXCEPTION 'Annotation edits retain identity and advance one revision'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderAnnotation_Mutation';
        END IF;
    END IF;
    IF TG_TABLE_NAME = 'ReaderHighlights' THEN
        IF NOT "IsReaderLocatorValid"(NEW."ReaderContentId", NEW."StartLocator")
            OR NOT "IsReaderLocatorValid"(NEW."ReaderContentId", NEW."EndLocator")
            OR NOT NEW."StartLocator" ? 'blockId' OR NOT NEW."EndLocator" ? 'blockId' THEN
            RAISE EXCEPTION 'Highlight requires two valid reflow text anchors'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderHighlights_Anchors';
        END IF;
        SELECT
            node."OrderIndex"
        INTO start_order
        FROM
            "ReaderContentNodes" AS node
        WHERE
            node."ReaderContentId" = NEW."ReaderContentId" AND node."StableId" = NEW."StartLocator"->>'blockId';
        SELECT
            node."OrderIndex"
        INTO end_order
        FROM
            "ReaderContentNodes" AS node
        WHERE
            node."ReaderContentId" = NEW."ReaderContentId" AND node."StableId" = NEW."EndLocator"->>'blockId';
        IF (start_order, (NEW."StartLocator"->>'characterOffset')::integer)
            >= (end_order, (NEW."EndLocator"->>'characterOffset')::integer) THEN
            RAISE EXCEPTION 'Highlight range must be nonempty and ordered'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderHighlights_Range';
        END IF;
        NEW."StartPosition" := start_order::bigint * 2147483648 + (NEW."StartLocator"->>'characterOffset')::integer;
        NEW."EndPosition" := end_order::bigint * 2147483648 + (NEW."EndLocator"->>'characterOffset')::integer;
    ELSIF NEW."ReaderContentId" IS NOT NULL AND NOT "IsReaderLocatorValid"(NEW."ReaderContentId", NEW."Locator") THEN
        RAISE EXCEPTION 'Reader locator is invalid for its exact published content revision'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_ReaderAnchor_ExactLocator';
    END IF;
    RETURN NEW;
END $reader_anchor$;
CREATE TRIGGER "TR_ReadingProgressPositions_Anchor" BEFORE INSERT OR UPDATE ON "ReadingProgressPositions"
    FOR EACH ROW EXECUTE FUNCTION "ValidateReaderAnchor"();
CREATE TRIGGER "TR_ReaderBookmarks_Anchor" BEFORE INSERT OR UPDATE ON "ReaderBookmarks"
    FOR EACH ROW EXECUTE FUNCTION "ValidateReaderAnchor"();
CREATE TRIGGER "TR_ReaderHighlights_Anchor" BEFORE INSERT OR UPDATE ON "ReaderHighlights"
    FOR EACH ROW EXECUTE FUNCTION "ValidateReaderAnchor"();

COMMIT;
