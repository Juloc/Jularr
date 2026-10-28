-- Jularr's own download-only torrent engine: a download client without an address or key, its own workspace and an optional module
-- that needs Acquisition and the VPN module; it starts disabled.
ALTER TABLE "InstanceModules" DROP CONSTRAINT "InstanceModules_Key_check";
ALTER TABLE "InstanceModules" ADD CONSTRAINT "InstanceModules_Key_check"
    CHECK ("Key" IN ('Library', 'Requests', 'Acquisition', 'Playback', 'Reading', 'Profiles', 'Emulation', 'Notifications',
        'Collections', 'Calendar', 'Statistics', 'UnitGroups', 'HomeLayout', 'Trickplay', 'Franchises', 'Diagnostics',
        'Subtitles', 'Uploads', 'ProgressSync', 'AniList', 'Devices', 'Registration', 'Translation', 'Ocr', 'SmartPdf',
        'DiscoverCache', 'LocalDiscovery', 'Browser', 'Vpn', 'Torrent'));
INSERT INTO "InstanceModules" ("Key", "IsEnabled") VALUES ('Torrent', FALSE)
ON CONFLICT ("Key") DO NOTHING;
INSERT INTO "InstanceModuleDependencies" ("ModuleKey", "RequiredKey") VALUES ('Torrent', 'Acquisition'), ('Torrent', 'Vpn')
ON CONFLICT DO NOTHING;

INSERT INTO "AcquisitionConnectionTypes" ("Id", "Key", "AcquisitionConnectionRoleId") VALUES (11, 'Torrent', 2)
ON CONFLICT ("Id") DO NOTHING;

-- The engine has neither a web address nor a key: the two unnamed baseline checks that require them are replaced by named ones that know it.
DO $$
DECLARE
    constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT check_constraint.conname
        FROM pg_constraint AS check_constraint
        WHERE check_constraint.conrelid = '"AcquisitionConnections"'::regclass
          AND check_constraint.contype = 'c'
          AND (pg_get_constraintdef(check_constraint.oid) LIKE '%"ProtectedApiKey" IS NOT NULL%'
               OR pg_get_constraintdef(check_constraint.oid) LIKE '%= 6) = ("BaseUrl" IS NULL)%')
    LOOP
        EXECUTE format('ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_TypeKey_check"
    CHECK ("AcquisitionConnectionTypeId" IN (1, 3, 4, 5, 6, 11) OR "ProtectedApiKey" IS NOT NULL);
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_TypeAddress_check"
    CHECK (("AcquisitionConnectionTypeId" IN (6, 11)) = ("BaseUrl" IS NULL));

ALTER TABLE "DownloadStorageSettings" DROP CONSTRAINT "DownloadStorageSettings_DownloadStorageAreaId_check";
ALTER TABLE "DownloadStorageSettings" ADD CONSTRAINT "DownloadStorageSettings_DownloadStorageAreaId_check"
    CHECK ("DownloadStorageAreaId" IN (1, 2, 3));
INSERT INTO "DownloadStorageSettings" ("DownloadStorageAreaId") VALUES (3)
ON CONFLICT ("DownloadStorageAreaId") DO NOTHING;
