BEGIN;
DO $reader_integrity$
DECLARE
    locale_id bigint;
    account_id bigint;
    profile_id bigint;
    work_id bigint;
    other_work_id bigint;
    edition_id bigint;
    other_edition_id bigint;
    content_id bigint;
    other_content_id bigint;
    progress_id bigint;
    version_id bigint;
    asset_id bigint;
    invalid_dimensions record;
    rejected_constraint text;
BEGIN
    INSERT INTO "UiLocales" ("Locale", "Name")
    VALUES ('phase-b-reader', 'Reader integrity')
    RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
    VALUES ('phase-b-reader@example.invalid', 'Reader integrity', 2)
    RETURNING "Id" INTO account_id;
    INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
    VALUES (account_id, 'Reader integrity', locale_id)
    RETURNING "Id" INTO profile_id;
    INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
    VALUES (account_id, profile_id);
    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
    VALUES (3, 'Reader work')
    RETURNING "Id" INTO work_id;
    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
    VALUES (3, 'Other reader work')
    RETURNING "Id" INTO other_work_id;
    INSERT INTO "WorkEditions" ("WorkId", "Name")
    VALUES (work_id, 'Reader edition')
    RETURNING "Id" INTO edition_id;
    INSERT INTO "WorkEditions" ("WorkId", "Name")
    VALUES (other_work_id, 'Other reader edition')
    RETURNING "Id" INTO other_edition_id;
    INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "StorageKey")
    VALUES (work_id, edition_id, 'phase-b-reader-content')
    RETURNING "Id" INTO content_id;
    INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "StorageKey")
    VALUES (other_work_id, other_edition_id, 'phase-b-other-reader-content')
    RETURNING "Id" INTO other_content_id;
    INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId")
    VALUES (profile_id, work_id, 2)
    RETURNING "Id" INTO progress_id;
    INSERT INTO "ReadingProgressPositions" ("MediaProgressId", "WorkId", "ReaderContentId", "PageIndex")
    VALUES (progress_id, work_id, content_id, 0);
    SET CONSTRAINTS ALL IMMEDIATE;

    BEGIN
        INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey")
        VALUES (other_work_id, edition_id, 2, 'phase-b-invalid-reader-content');
        RAISE EXCEPTION 'ReaderContent accepted a foreign WorkEdition';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_ReaderContent_WorkEditions' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        UPDATE "ReadingProgressPositions"
        SET "ReaderContentId" = other_content_id
        WHERE "MediaProgressId" = progress_id;
        RAISE EXCEPTION 'Reading progress accepted foreign ReaderContent';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_ReadingProgressPositions_ReaderContent' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        UPDATE "ReadingProgressPositions"
        SET "WorkId" = other_work_id, "ReaderContentId" = NULL
        WHERE "MediaProgressId" = progress_id;
        RAISE EXCEPTION 'Reading position accepted a foreign parent Work';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_ReadingProgressPositions_ProgressWork' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        UPDATE "MediaProgress"
        SET "WorkId" = other_work_id
        WHERE "Id" = progress_id;
        RAISE EXCEPTION 'Parent Work update detached the reading position';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_ReadingProgressPositions_ProgressWork' THEN
            RAISE;
        END IF;
    END;

    UPDATE "ReadingProgressPositions"
    SET "ReaderContentId" = NULL
    WHERE "MediaProgressId" = progress_id;
    UPDATE "ReadingProgressPositions"
    SET "ReaderContentId" = content_id
    WHERE "MediaProgressId" = progress_id;

    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId")
    VALUES (work_id, edition_id)
    RETURNING "Id" INTO version_id;
    INSERT INTO "MediaAssetTypes" ("Id", "Key") VALUES (1, 'phase-b-reader');
    INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId")
    VALUES (work_id, version_id, 1)
    RETURNING "Id" INTO asset_id;
    INSERT INTO "Images" ("StorageKey", "MimeType")
    VALUES ('phase-b-unknown-dimensions', 'image/png');
    INSERT INTO "Images" ("StorageKey", "MimeType", "Width", "Height")
    VALUES ('phase-b-known-dimensions', 'image/png', 1920, 1080);
    INSERT INTO "MediaTechnicalAnalyses" ("MediaAssetId") VALUES (asset_id);
    UPDATE "MediaTechnicalAnalyses"
    SET "Width" = 1920, "Height" = 1080
    WHERE "MediaAssetId" = asset_id;

    FOR invalid_dimensions IN
        SELECT
            dimensions.width,
            dimensions.height
        FROM (VALUES
            (NULL::integer, 1080),
            (1920, NULL::integer),
            (0, 1080),
            (1920, 0),
            (-1, 1080),
            (1920, -1)
        ) AS dimensions(width, height)
    LOOP
        BEGIN
            INSERT INTO "Images" ("StorageKey", "MimeType", "Width", "Height")
            VALUES ('phase-b-invalid-dimensions', 'image/png', invalid_dimensions.width, invalid_dimensions.height);
            RAISE EXCEPTION 'Image accepted invalid dimensions: %, %', invalid_dimensions.width, invalid_dimensions.height;
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_Images_Dimensions' THEN
                RAISE;
            END IF;
        END;

        BEGIN
            UPDATE "MediaTechnicalAnalyses"
            SET "Width" = invalid_dimensions.width, "Height" = invalid_dimensions.height
            WHERE "MediaAssetId" = asset_id;
            RAISE EXCEPTION 'Analysis accepted invalid dimensions: %, %', invalid_dimensions.width, invalid_dimensions.height;
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_MediaTechnicalAnalyses_Dimensions' THEN
                RAISE;
            END IF;
        END;
    END LOOP;
END $reader_integrity$;
ROLLBACK;
