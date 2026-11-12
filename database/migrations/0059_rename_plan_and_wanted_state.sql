-- The library rename plan, built by the worker after scans, downloads and naming or assignment changes, at the latest an hour after the last build, and
-- on the Owner's request, instead of on every visit of the Rename tab. One plan for all libraries; its rows in the order the tab lists them.
-- Applying builds the plan again and refuses it when it differs from the one shown. Empty until the first build.
CREATE TABLE "LibraryRenamePlans" (
    "Id" smallint PRIMARY KEY CHECK ("Id" = 1),
    "Fingerprint" varchar(64) NOT NULL,
    "RenameCount" integer NOT NULL CHECK ("RenameCount" >= 0),
    "BuiltAt" timestamptz NOT NULL,
    -- Something the plan reads changed since; the worker builds it again.
    "RequestedAt" timestamptz NULL
);

-- A copy of the plan at "BuiltAt": a file renamed or deleted since keeps its row until the next build.
CREATE TABLE "LibraryRenamePlanItems" (
    "Position" integer PRIMARY KEY CHECK ("Position" > 0),
    "StoredFileId" bigint NOT NULL,
    "MediaStoredFileId" bigint NOT NULL,
    "LibraryRootId" bigint NOT NULL,
    "LibraryRootName" varchar(128) NOT NULL,
    "RelativePath" text NOT NULL,
    "TargetPath" text NOT NULL
);

-- What the Wanted list shows of every target, worked out by the worker instead of on every visit: whether it has its media, is below its cutoff
-- or lacks a first language (the views WantedBelowCutoff and WantedVideoLanguages), and for a book each language it is wanted in (WantedBookLanguages).
CREATE TABLE "WantedItemStates" (
    "WantedItemId" bigint PRIMARY KEY REFERENCES "WantedItems" ("Id") ON DELETE CASCADE,
    "HasMedia" boolean NOT NULL,
    "IsCutoffUnmet" boolean NOT NULL,
    "IsLanguageOpen" boolean NOT NULL
);

CREATE TABLE "WantedBookLanguageStates" (
    "WantedItemId" bigint NOT NULL REFERENCES "WantedItems" ("Id") ON DELETE CASCADE,
    "Language" text NOT NULL,
    "IsCovered" boolean NOT NULL,
    PRIMARY KEY ("WantedItemId", "Language")
);

-- When the states were worked out: the moment their reading saw the data. Empty until the first time.
CREATE TABLE "WantedStateRefreshes" (
    "Id" smallint PRIMARY KEY CHECK ("Id" = 1),
    "RefreshedAt" timestamptz NOT NULL
);

-- Something the states read changed: committed with the change, removed by the refresh that read it. While one is open the list works the states out
-- as before, so it is never behind; the worker refreshes soon after.
CREATE TABLE "WantedStateRequests" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "RequestedAt" timestamptz NOT NULL DEFAULT now()
);

-- One open request is enough; a transaction that changes many rows adds it once.
CREATE FUNCTION "RequestWantedState"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "WantedStateRequests" ("RequestedAt") SELECT now() WHERE NOT EXISTS (SELECT 1 FROM "WantedStateRequests");
    RETURN NULL;
END;
$$;

-- Catalog titles change all the time; only a title with a target matters.
CREATE FUNCTION "RequestWantedStateOfWork"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('INSERT', 'UPDATE') AND EXISTS (SELECT 1 FROM "WantedItems" WHERE "WorkId" = NEW."WorkId")
        OR TG_OP IN ('UPDATE', 'DELETE') AND EXISTS (SELECT 1 FROM "WantedItems" WHERE "WorkId" = OLD."WorkId") THEN
        INSERT INTO "WantedStateRequests" ("RequestedAt") SELECT now() WHERE NOT EXISTS (SELECT 1 FROM "WantedStateRequests");
    END IF;

    RETURN NULL;
END;
$$;

CREATE FUNCTION "RequestWantedStateOfTitle"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM "WantedItems" WHERE "WorkId" = NEW."Id") THEN
        INSERT INTO "WantedStateRequests" ("RequestedAt") SELECT now() WHERE NOT EXISTS (SELECT 1 FROM "WantedStateRequests");
    END IF;

    RETURN NULL;
END;
$$;

-- Row triggers: a statement that changes nothing asks nothing. Busy tables ask only when a column the states read changes, quiet ones on any change.
CREATE TRIGGER "TR_StoredFiles_WantedState_Change" AFTER INSERT OR DELETE ON "StoredFiles" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_StoredFiles_WantedState_Update" AFTER UPDATE ON "StoredFiles" FOR EACH ROW
    WHEN ((OLD."MediaAssetId", OLD."MissingSince", OLD."QualityKey", OLD."ReleaseLanguages", OLD."ReleaseName", OLD."RelativePath", OLD."LibraryRootId", OLD."SizeBytes")
        IS DISTINCT FROM (NEW."MediaAssetId", NEW."MissingSince", NEW."QualityKey", NEW."ReleaseLanguages", NEW."ReleaseName", NEW."RelativePath", NEW."LibraryRootId", NEW."SizeBytes"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_MediaAssets_WantedState_Change" AFTER INSERT OR DELETE ON "MediaAssets" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_MediaAssets_WantedState_Update" AFTER UPDATE ON "MediaAssets" FOR EACH ROW
    WHEN ((OLD."WorkId", OLD."WorkVersionId", OLD."WorkUnitId")
        IS DISTINCT FROM (NEW."WorkId", NEW."WorkVersionId", NEW."WorkUnitId"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_WorkUnits_WantedState_Change" AFTER INSERT OR DELETE ON "WorkUnits" FOR EACH ROW EXECUTE FUNCTION "RequestWantedStateOfWork"();
CREATE TRIGGER "TR_WorkUnits_WantedState_Update" AFTER UPDATE ON "WorkUnits" FOR EACH ROW
    WHEN ((OLD."WorkId", OLD."IsMonitored", OLD."ReleasedOn", OLD."WorkUnitTypeId")
        IS DISTINCT FROM (NEW."WorkId", NEW."IsMonitored", NEW."ReleasedOn", NEW."WorkUnitTypeId"))
    EXECUTE FUNCTION "RequestWantedStateOfWork"();
CREATE TRIGGER "TR_WantedItems_WantedState_Change" AFTER INSERT OR DELETE ON "WantedItems" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_WantedItems_WantedState_Update" AFTER UPDATE ON "WantedItems" FOR EACH ROW
    WHEN ((OLD."WorkId", OLD."GamePlatformId", OLD."AcquisitionProfileId")
        IS DISTINCT FROM (NEW."WorkId", NEW."GamePlatformId", NEW."AcquisitionProfileId"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionReleases_WantedState_Change" AFTER INSERT OR DELETE ON "AcquisitionReleases" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionReleases_WantedState_Update" AFTER UPDATE ON "AcquisitionReleases" FOR EACH ROW
    WHEN ((OLD."WantedItemId", OLD."Title", OLD."TargetLanguage", OLD."IndexerFlags")
        IS DISTINCT FROM (NEW."WantedItemId", NEW."Title", NEW."TargetLanguage", NEW."IndexerFlags"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionRequests_WantedState_Change" AFTER INSERT OR DELETE ON "AcquisitionRequests" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionRequests_WantedState_Update" AFTER UPDATE ON "AcquisitionRequests" FOR EACH ROW
    WHEN ((OLD."AcquisitionRequestStatusTypeId", OLD."UiLocaleId", OLD."RequestedByProfileId")
        IS DISTINCT FROM (NEW."AcquisitionRequestStatusTypeId", NEW."UiLocaleId", NEW."RequestedByProfileId"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_Works_WantedState_Update" AFTER UPDATE ON "Works" FOR EACH ROW
    WHEN ((OLD."MediaTypeId", OLD."MediaSubtypeId", OLD."OriginalLanguage", OLD."ReleaseYear")
        IS DISTINCT FROM (NEW."MediaTypeId", NEW."MediaSubtypeId", NEW."OriginalLanguage", NEW."ReleaseYear"))
    EXECUTE FUNCTION "RequestWantedStateOfTitle"();
CREATE TRIGGER "TR_Profiles_WantedState_Change" AFTER INSERT OR DELETE ON "Profiles" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_Profiles_WantedState_Update" AFTER UPDATE ON "Profiles" FOR EACH ROW
    WHEN ((OLD."FirstContentLanguage", OLD."SecondContentLanguage", OLD."LanguageUsesOriginal", OLD."UiLocaleId")
        IS DISTINCT FROM (NEW."FirstContentLanguage", NEW."SecondContentLanguage", NEW."LanguageUsesOriginal", NEW."UiLocaleId"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_InstanceSettings_WantedState_Update" AFTER UPDATE ON "InstanceSettings" FOR EACH ROW
    WHEN ((OLD."MonitoredLanguageModeId", OLD."MonitoredFirstLanguage", OLD."MonitoredSecondLanguage", OLD."MonitoredUsesOriginal")
        IS DISTINCT FROM (NEW."MonitoredLanguageModeId", NEW."MonitoredFirstLanguage", NEW."MonitoredSecondLanguage", NEW."MonitoredUsesOriginal"))
    EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_MediaAssetAdditionalUnits_WantedState_Change" AFTER INSERT OR DELETE ON "MediaAssetAdditionalUnits" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_MediaAssetAdditionalUnits_WantedState_Update" AFTER UPDATE ON "MediaAssetAdditionalUnits" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_GameReleases_WantedState_Change" AFTER INSERT OR DELETE ON "GameReleases" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_GameReleases_WantedState_Update" AFTER UPDATE ON "GameReleases" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfiles_WantedState_Change" AFTER INSERT OR DELETE ON "AcquisitionProfiles" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfiles_WantedState_Update" AFTER UPDATE ON "AcquisitionProfiles" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfileMediaKinds_WantedState_Change" AFTER INSERT OR DELETE ON "AcquisitionProfileMediaKinds" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfileMediaKinds_WantedState_Update" AFTER UPDATE ON "AcquisitionProfileMediaKinds" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfileDefaults_WantedState_Change" AFTER INSERT OR DELETE ON "AcquisitionProfileDefaults" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_AcquisitionProfileDefaults_WantedState_Update" AFTER UPDATE ON "AcquisitionProfileDefaults" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_CustomFormats_WantedState_Change" AFTER INSERT OR DELETE ON "CustomFormats" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_CustomFormats_WantedState_Update" AFTER UPDATE ON "CustomFormats" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_StoredFileCustomFormats_WantedState_Change" AFTER INSERT OR DELETE ON "StoredFileCustomFormats" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_StoredFileCustomFormats_WantedState_Update" AFTER UPDATE ON "StoredFileCustomFormats" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_RequestTargets_WantedState_Change" AFTER INSERT OR DELETE ON "RequestTargets" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_RequestTargets_WantedState_Update" AFTER UPDATE ON "RequestTargets" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_UiLocales_WantedState_Change" AFTER INSERT OR DELETE ON "UiLocales" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_UiLocales_WantedState_Update" AFTER UPDATE ON "UiLocales" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_WantedItemLanguageChains_WantedState_Change" AFTER INSERT OR DELETE ON "WantedItemLanguageChains" FOR EACH ROW EXECUTE FUNCTION "RequestWantedState"();
CREATE TRIGGER "TR_WantedItemLanguageChains_WantedState_Update" AFTER UPDATE ON "WantedItemLanguageChains" FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*) EXECUTE FUNCTION "RequestWantedState"();
