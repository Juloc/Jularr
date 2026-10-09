using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <summary>
    /// The canonical Work identity becomes a BIGINT identity. Every Work gets its number from a temporary old-GUID to number map
    /// (<c>WorkIdMigrationMap</c>, dropped by the application once the file-based stores that name Works are converted), every column that names a Work is
    /// retyped through it with its indexes and constraints rebuilt, and the places that stored the old GUID as text (request payloads and links, operation
    /// targets, notification links, reader scopes) are rewritten with the new number. A Work target of a monitoring, request or wanted row has no node id
    /// any more: its <c>TargetId</c> is NULL and the row's <c>WorkId</c> names it. The whole migration is one transaction and fails, rather than losing a
    /// reference, when a column names a Work that does not exist.
    /// </summary>
    public partial class WorkIdInt64 : Migration
    {
        private static readonly string[] WorkColumns =
        [
            "ActiveSessions.WorkId", "CollectionItems.WorkId", "LibraryReconciliationFileLinks.WorkId", "LibraryReconciliationPlanItems.AssignedWorkId",
            "MediaAssets.WorkId", "MediaPlaybackHistory.WorkId", "MediaProgress.WorkId", "MusicAlbums.WorkId", "RequestTargets.WorkId", "WantedItems.WorkId",
            "WorkArtwork.WorkId", "WorkChapters.WorkId", "WorkCredits.WorkId", "WorkEditions.WorkId", "WorkEpisodes.WorkId", "WorkExternalIdentities.WorkId",
            "WorkFieldProvenance.WorkId", "WorkIdentityChanges.SourceWorkId", "WorkIdentityChanges.TargetWorkId", "WorkLocalizedValues.WorkId",
            "WorkMetadataFacts.WorkId", "WorkMetadataRefreshes.WorkId", "WorkMonitoring.WorkId", "WorkRelations.FromWorkId", "WorkRelations.ToWorkId",
            "WorkSeasons.WorkId", "WorkSourceLinks.WorkId", "WorkTitles.WorkId", "WorkTracks.WorkId", "WorkUnitBindings.WorkId", "WorkVersions.WorkId",
            "WorkVolumes.WorkId"
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "WorkIdMigrationMap" (
                    "OldId" uuid NOT NULL CONSTRAINT "PK_WorkIdMigrationMap" PRIMARY KEY,
                    "NewId" bigint GENERATED ALWAYS AS IDENTITY NOT NULL CONSTRAINT "UQ_WorkIdMigrationMap_NewId" UNIQUE
                );
                INSERT INTO "WorkIdMigrationMap" ("OldId") SELECT "Id" FROM "Works" ORDER BY "CreatedAt", "Id";

                CREATE TEMP TABLE work_fk_saved ON COMMIT DROP AS
                SELECT conrelid::regclass::text AS tbl, conname, pg_get_constraintdef(oid) AS def
                FROM pg_constraint WHERE contype = 'f' AND confrelid = '"Works"'::regclass;

                CREATE TEMP TABLE work_ddl_saved (ord serial, ddl text NOT NULL) ON COMMIT DROP;
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION pg_temp.retype_work_column(tbl text, col text, tolerate_unmatched boolean DEFAULT false) RETURNS void LANGUAGE plpgsql AS $fn$
                DECLARE
                    rel regclass := format('public.%I', tbl)::regclass;
                    col_number smallint;
                    col_type text;
                    col_nullable boolean;
                    unmatched bigint;
                    saved record;
                BEGIN
                    SELECT a.attnum, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull INTO col_number, col_type, col_nullable
                    FROM pg_attribute a WHERE a.attrelid = rel AND a.attname = col AND NOT a.attisdropped;

                    -- What is built on the column is rebuilt after the swap: constraints first, then the indexes that do not back a constraint.
                    DELETE FROM work_ddl_saved;
                    INSERT INTO work_ddl_saved (ddl)
                    SELECT format('ALTER TABLE %s ADD CONSTRAINT %I %s', rel, c.conname, pg_get_constraintdef(c.oid))
                    FROM pg_constraint c WHERE c.conrelid = rel AND c.contype IN ('p', 'u', 'c') AND col_number = ANY (c.conkey)
                    ORDER BY CASE c.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 ELSE 2 END, c.conname;
                    INSERT INTO work_ddl_saved (ddl)
                    SELECT pg_get_indexdef(x.indexrelid) FROM pg_index x
                    WHERE x.indrelid = rel AND col_number = ANY (x.indkey::int2[])
                      AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = x.indexrelid)
                    ORDER BY x.indexrelid;

                    EXECUTE format('ALTER TABLE %s ADD COLUMN %I bigint', rel, col || '__new');
                    EXECUTE format('UPDATE %s t SET %I = m."NewId" FROM "WorkIdMigrationMap" m WHERE %s', rel, col || '__new',
                                   CASE WHEN col_type = 'uuid' THEN format('m."OldId" = t.%I', col) ELSE format('m."OldId"::text = t.%I', col) END);
                    EXECUTE format('SELECT count(*) FROM %s t WHERE t.%I IS NOT NULL AND t.%I IS NULL', rel, col, col || '__new') INTO unmatched;
                    IF unmatched > 0 AND NOT tolerate_unmatched THEN
                        RAISE EXCEPTION '%.% names % Work(s) that do not exist; the migration stops instead of losing the reference.', tbl, col, unmatched;
                    END IF;

                    EXECUTE format('ALTER TABLE %s DROP COLUMN %I', rel, col);
                    EXECUTE format('ALTER TABLE %s RENAME COLUMN %I TO %I', rel, col || '__new', col);
                    IF NOT col_nullable THEN
                        EXECUTE format('ALTER TABLE %s ALTER COLUMN %I SET NOT NULL', rel, col);
                    END IF;

                    FOR saved IN SELECT ddl FROM work_ddl_saved ORDER BY ord LOOP
                        EXECUTE saved.ddl;
                    END LOOP;
                END
                $fn$;

                CREATE FUNCTION pg_temp.remap_work_ids(input text, json_property boolean) RETURNS text LANGUAGE plpgsql AS $fn$
                DECLARE
                    token text[];
                    result text := input;
                    new_id bigint;
                BEGIN
                    IF input IS NULL THEN
                        RETURN NULL;
                    END IF;
                    FOR token IN SELECT DISTINCT regexp_matches(lower(input), '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', 'g') LOOP
                        SELECT "NewId" INTO new_id FROM "WorkIdMigrationMap" WHERE "OldId" = token[1]::uuid;
                        IF new_id IS NOT NULL THEN
                            IF json_property THEN
                                result := regexp_replace(result, '("workId"\s*:\s*)"' || token[1] || '"', '\1' || new_id::text, 'gi');
                            ELSE
                                result := regexp_replace(result, token[1], new_id::text, 'gi');
                            END IF;
                        END IF;
                    END LOOP;
                    RETURN result;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE saved record;
                BEGIN
                    FOR saved IN SELECT tbl, conname FROM work_fk_saved LOOP
                        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', saved.tbl, saved.conname);
                    END LOOP;
                END $$;
                """);

            foreach (var column in WorkColumns)
            {
                var parts = column.Split('.');
                migrationBuilder.Sql($"SELECT pg_temp.retype_work_column('{parts[0]}', '{parts[1]}');");
            }

            migrationBuilder.Sql(
                """
                SELECT pg_temp.retype_work_column('AcquisitionRequests', 'WorkId', tolerate_unmatched => true);
                SELECT pg_temp.retype_work_column('Works', 'Id');
                ALTER TABLE "Works" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"Works"', 'Id'), (SELECT COALESCE(MAX("Id"), 0) + 1 FROM "Works"), false);

                DO $$
                DECLARE saved record;
                BEGIN
                    FOR saved IN SELECT tbl, conname, def FROM work_fk_saved ORDER BY tbl, conname LOOP
                        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I %s', saved.tbl, saved.conname, saved.def);
                    END LOOP;
                    IF (SELECT count(*) FROM "Works") <> (SELECT count(*) FROM "WorkIdMigrationMap") THEN
                        RAISE EXCEPTION 'The Work number map does not cover every Work.';
                    END IF;
                END $$;
                """);

            // The Work itself is a target without a node id: the row's WorkId names it.
            migrationBuilder.Sql(
                """
                ALTER TABLE "WorkMonitoring" ALTER COLUMN "TargetId" DROP NOT NULL;
                UPDATE "WorkMonitoring" SET "TargetId" = NULL WHERE "Kind" = 0;
                ALTER TABLE "WorkMonitoring" ADD CONSTRAINT "CK_WorkMonitoring_WorkDecisionHasNoNode" CHECK (("Kind" = 0) = ("TargetId" IS NULL));
                CREATE UNIQUE INDEX "UQ_WorkMonitoring_WorkDecision" ON "WorkMonitoring" ("WorkId") WHERE "Kind" = 0;

                ALTER TABLE "RequestTargets" ALTER COLUMN "TargetId" DROP NOT NULL;
                UPDATE "RequestTargets" SET "TargetId" = NULL WHERE "TargetKind" = 0;
                ALTER TABLE "RequestTargets" DROP CONSTRAINT "UQ_RequestTargets_Target";
                ALTER TABLE "RequestTargets" ADD CONSTRAINT "UQ_RequestTargets_Target" UNIQUE NULLS NOT DISTINCT ("RequestId", "TargetKind", "WorkId", "TargetId");
                ALTER TABLE "RequestTargets" ADD CONSTRAINT "CK_RequestTargets_WorkTargetHasNoNode" CHECK (("TargetKind" = 0) = ("TargetId" IS NULL));

                ALTER TABLE "WantedItems" ALTER COLUMN "TargetId" DROP NOT NULL;
                UPDATE "WantedItems" SET "TargetId" = NULL WHERE "TargetKind" = 0;
                DROP INDEX "IX_WantedItems_TargetKind_TargetId";
                DROP INDEX "IX_WantedItems_WorkId";
                CREATE INDEX "IX_WantedItems_TargetKind_TargetId" ON "WantedItems" ("TargetKind", "TargetId");
                CREATE UNIQUE INDEX "IX_WantedItems_WorkId_TargetKind_TargetId" ON "WantedItems" ("WorkId", "TargetKind", "TargetId") NULLS NOT DISTINCT;
                ALTER TABLE "WantedItems" ADD CONSTRAINT "CK_WantedItems_WorkTargetHasNoNode" CHECK (("TargetKind" = 0) = ("TargetId" IS NULL));
                """);

            // Where a Work was stored as text: payloads and links of requests, operation targets, notification links, merge history and the reader scope of a Work.
            // The Discover snapshots are a cache of provider answers that carry the old addresses, so they are rebuilt.
            migrationBuilder.Sql(
                """
                UPDATE "AcquisitionRequests" SET "PayloadJson" = pg_temp.remap_work_ids("PayloadJson", true) WHERE "PayloadJson" ~* '"workId"\s*:\s*"';
                UPDATE "AcquisitionRequests" SET "ResultUrl" = pg_temp.remap_work_ids("ResultUrl", false) WHERE "ResultUrl" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "Operations" SET "Details" = pg_temp.remap_work_ids("Details", false) WHERE "Details" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "Events" SET "DeepLink" = pg_temp.remap_work_ids("DeepLink", false), "DedupKey" = pg_temp.remap_work_ids("DedupKey", false),
                       "MessageParamsJson" = pg_temp.remap_work_ids("MessageParamsJson", false)
                WHERE "DeepLink" ~* '[0-9a-f]{8}-[0-9a-f]{4}-' OR "DedupKey" ~* '[0-9a-f]{8}-[0-9a-f]{4}-' OR "MessageParamsJson" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "Notifications" SET "DeepLink" = pg_temp.remap_work_ids("DeepLink", false), "DedupKey" = pg_temp.remap_work_ids("DedupKey", false),
                       "MessageParamsJson" = pg_temp.remap_work_ids("MessageParamsJson", false)
                WHERE "DeepLink" ~* '[0-9a-f]{8}-[0-9a-f]{4}-' OR "DedupKey" ~* '[0-9a-f]{8}-[0-9a-f]{4}-' OR "MessageParamsJson" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "WorkIdentityChanges" SET "Details" = pg_temp.remap_work_ids("Details", false) WHERE "Details" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "FranchiseMembers" SET "DetailsUrl" = pg_temp.remap_work_ids("DetailsUrl", false) WHERE "DetailsUrl" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "ProfileWatchlistPreferences" SET "DetailsUrl" = pg_temp.remap_work_ids("DetailsUrl", false) WHERE "DetailsUrl" ~* '[0-9a-f]{8}-[0-9a-f]{4}-';
                UPDATE "ReaderPreferences" r SET "ScopeKey" = 'work:' || m."NewId" FROM "WorkIdMigrationMap" m WHERE r."ScopeKey" = 'work:' || replace(m."OldId"::text, '-', '');
                DELETE FROM "DiscoverySnapshots";
                """);
        }

        // The way back: every Work gets a fresh GUID (the original ones are not kept) and everything that was converted is converted back through it.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "WorkIdMigrationMap";
                CREATE TABLE "WorkIdMigrationMap" (
                    "NewId" bigint NOT NULL CONSTRAINT "PK_WorkIdMigrationMap" PRIMARY KEY,
                    "OldId" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "UQ_WorkIdMigrationMap_OldId" UNIQUE
                );
                INSERT INTO "WorkIdMigrationMap" ("NewId") SELECT "Id" FROM "Works";

                CREATE TEMP TABLE work_fk_saved ON COMMIT DROP AS
                SELECT conrelid::regclass::text AS tbl, conname, pg_get_constraintdef(oid) AS def
                FROM pg_constraint WHERE contype = 'f' AND confrelid = '"Works"'::regclass;

                CREATE TEMP TABLE work_ddl_saved (ord serial, ddl text NOT NULL) ON COMMIT DROP;

                -- A Work target has its node id again: the Work's own GUID.
                ALTER TABLE "WorkMonitoring" DROP CONSTRAINT "CK_WorkMonitoring_WorkDecisionHasNoNode";
                DROP INDEX "UQ_WorkMonitoring_WorkDecision";
                UPDATE "WorkMonitoring" t SET "TargetId" = m."OldId" FROM "WorkIdMigrationMap" m WHERE t."Kind" = 0 AND m."NewId" = t."WorkId";
                ALTER TABLE "WorkMonitoring" ALTER COLUMN "TargetId" SET NOT NULL;

                ALTER TABLE "RequestTargets" DROP CONSTRAINT "CK_RequestTargets_WorkTargetHasNoNode";
                ALTER TABLE "RequestTargets" DROP CONSTRAINT "UQ_RequestTargets_Target";
                UPDATE "RequestTargets" t SET "TargetId" = m."OldId" FROM "WorkIdMigrationMap" m WHERE t."TargetKind" = 0 AND m."NewId" = t."WorkId";
                ALTER TABLE "RequestTargets" ALTER COLUMN "TargetId" SET NOT NULL;
                ALTER TABLE "RequestTargets" ADD CONSTRAINT "UQ_RequestTargets_Target" UNIQUE ("RequestId", "TargetKind", "TargetId");

                ALTER TABLE "WantedItems" DROP CONSTRAINT "CK_WantedItems_WorkTargetHasNoNode";
                DROP INDEX "IX_WantedItems_WorkId_TargetKind_TargetId";
                DROP INDEX "IX_WantedItems_TargetKind_TargetId";
                UPDATE "WantedItems" t SET "TargetId" = m."OldId" FROM "WorkIdMigrationMap" m WHERE t."TargetKind" = 0 AND m."NewId" = t."WorkId";
                ALTER TABLE "WantedItems" ALTER COLUMN "TargetId" SET NOT NULL;
                CREATE UNIQUE INDEX "IX_WantedItems_TargetKind_TargetId" ON "WantedItems" ("TargetKind", "TargetId");
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION pg_temp.retype_work_column_back(tbl text, col text, new_type text DEFAULT 'uuid') RETURNS void LANGUAGE plpgsql AS $fn$
                DECLARE
                    rel regclass := format('public.%I', tbl)::regclass;
                    col_number smallint;
                    col_nullable boolean;
                    unmatched bigint;
                    saved record;
                BEGIN
                    SELECT a.attnum, NOT a.attnotnull INTO col_number, col_nullable
                    FROM pg_attribute a WHERE a.attrelid = rel AND a.attname = col AND NOT a.attisdropped;

                    DELETE FROM work_ddl_saved;
                    INSERT INTO work_ddl_saved (ddl)
                    SELECT format('ALTER TABLE %s ADD CONSTRAINT %I %s', rel, c.conname, pg_get_constraintdef(c.oid))
                    FROM pg_constraint c WHERE c.conrelid = rel AND c.contype IN ('p', 'u', 'c') AND col_number = ANY (c.conkey)
                    ORDER BY CASE c.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 ELSE 2 END, c.conname;
                    INSERT INTO work_ddl_saved (ddl)
                    SELECT pg_get_indexdef(x.indexrelid) FROM pg_index x
                    WHERE x.indrelid = rel AND col_number = ANY (x.indkey::int2[])
                      AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = x.indexrelid)
                    ORDER BY x.indexrelid;

                    EXECUTE format('ALTER TABLE %s ADD COLUMN %I %s', rel, col || '__old', new_type);
                    EXECUTE format('UPDATE %s t SET %I = %s FROM "WorkIdMigrationMap" m WHERE m."NewId" = t.%I', rel, col || '__old',
                                   CASE WHEN new_type = 'text' THEN 'm."OldId"::text' ELSE 'm."OldId"' END, col);
                    EXECUTE format('SELECT count(*) FROM %s t WHERE t.%I IS NOT NULL AND t.%I IS NULL', rel, col, col || '__old') INTO unmatched;
                    IF unmatched > 0 AND new_type <> 'text' THEN
                        RAISE EXCEPTION '%.% names % Work(s) that do not exist.', tbl, col, unmatched;
                    END IF;

                    EXECUTE format('ALTER TABLE %s DROP COLUMN %I', rel, col);
                    EXECUTE format('ALTER TABLE %s RENAME COLUMN %I TO %I', rel, col || '__old', col);
                    IF NOT col_nullable THEN
                        EXECUTE format('ALTER TABLE %s ALTER COLUMN %I SET NOT NULL', rel, col);
                    END IF;

                    FOR saved IN SELECT ddl FROM work_ddl_saved ORDER BY ord LOOP
                        EXECUTE saved.ddl;
                    END LOOP;
                END
                $fn$;

                CREATE FUNCTION pg_temp.restore_work_ids(input text) RETURNS text LANGUAGE plpgsql AS $fn$
                DECLARE
                    hit text[];
                    result text := input;
                    old_id uuid;
                BEGIN
                    IF input IS NULL THEN
                        RETURN NULL;
                    END IF;
                    FOR hit IN SELECT DISTINCT regexp_matches(input, '("workId"\s*:\s*|/Library/(?:Movie|Series|Watch)/|/Admin/Media/[a-z]+/|work:)([0-9]+)', 'g') LOOP
                        SELECT "OldId" INTO old_id FROM "WorkIdMigrationMap" WHERE "NewId" = hit[2]::bigint;
                        IF old_id IS NOT NULL THEN
                            result := replace(result, hit[1] || hit[2], hit[1] || CASE WHEN hit[1] LIKE '"workId"%' THEN '"' || old_id::text || '"' ELSE old_id::text END);
                        END IF;
                    END LOOP;
                    RETURN result;
                END
                $fn$;

                DO $$
                DECLARE saved record;
                BEGIN
                    FOR saved IN SELECT tbl, conname FROM work_fk_saved LOOP
                        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', saved.tbl, saved.conname);
                    END LOOP;
                END $$;
                """);

            foreach (var column in WorkColumns)
            {
                var parts = column.Split('.');
                migrationBuilder.Sql($"SELECT pg_temp.retype_work_column_back('{parts[0]}', '{parts[1]}');");
            }

            migrationBuilder.Sql(
                """
                SELECT pg_temp.retype_work_column_back('AcquisitionRequests', 'WorkId', 'text');
                SELECT pg_temp.retype_work_column_back('Works', 'Id');
                CREATE INDEX "IX_WantedItems_WorkId" ON "WantedItems" ("WorkId");

                DO $$
                DECLARE saved record;
                BEGIN
                    FOR saved IN SELECT tbl, conname, def FROM work_fk_saved ORDER BY tbl, conname LOOP
                        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I %s', saved.tbl, saved.conname, saved.def);
                    END LOOP;
                END $$;

                UPDATE "AcquisitionRequests" SET "PayloadJson" = pg_temp.restore_work_ids("PayloadJson") WHERE "PayloadJson" ~ '"workId"\s*:\s*[0-9]';
                UPDATE "AcquisitionRequests" SET "ResultUrl" = pg_temp.restore_work_ids("ResultUrl") WHERE "ResultUrl" ~ '[0-9]';
                UPDATE "Operations" SET "Details" = pg_temp.restore_work_ids("Details") WHERE "Details" ~ 'work:[0-9]';
                UPDATE "Events" SET "DeepLink" = pg_temp.restore_work_ids("DeepLink") WHERE "DeepLink" ~ '[0-9]';
                UPDATE "Notifications" SET "DeepLink" = pg_temp.restore_work_ids("DeepLink") WHERE "DeepLink" ~ '[0-9]';
                UPDATE "ReaderPreferences" r SET "ScopeKey" = 'work:' || replace(m."OldId"::text, '-', '') FROM "WorkIdMigrationMap" m WHERE r."ScopeKey" = 'work:' || m."NewId";
                DROP TABLE "WorkIdMigrationMap";
                """);
        }
    }
}
