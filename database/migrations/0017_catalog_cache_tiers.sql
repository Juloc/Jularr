-- The cache tier of each catalog replaces its "keep whole" switch and the DiscoverCache module: a catalog kept whole is tier 3,
-- without DiscoverCache Discover kept no lists (tier 1), otherwise it kept them (tier 2).
UPDATE "ExternalWorkSyncs" AS sync
SET
    "CacheTier" = CASE
        WHEN sync."IsEnabled" THEN 3
        WHEN EXISTS (SELECT 1 FROM "InstanceModules" AS module WHERE module."Key" = 'DiscoverCache' AND NOT module."IsEnabled") THEN 1
        ELSE 2
    END,
    "UpdatedAt" = now();

ALTER TABLE "ExternalWorkSyncs"
    DROP COLUMN "IsEnabled",
    DROP COLUMN "DetailsTrending";

-- One switch for every catalog and every Discover list: titles in a Discover list get their details in advance.
ALTER TABLE "InstanceSettings"
    ADD COLUMN IF NOT EXISTS "DetailsDiscoverAhead" boolean NOT NULL DEFAULT FALSE;

-- The module goes with its dependency (LocalDiscovery needed it).
DELETE FROM "InstanceModules" WHERE "Key" = 'DiscoverCache';
ALTER TABLE "InstanceModules" DROP CONSTRAINT "InstanceModules_Key_check";
ALTER TABLE "InstanceModules" ADD CONSTRAINT "InstanceModules_Key_check"
    CHECK ("Key" IN ('Library', 'Requests', 'Acquisition', 'Playback', 'Reading', 'Profiles', 'Emulation', 'Notifications',
        'Collections', 'Calendar', 'Statistics', 'UnitGroups', 'HomeLayout', 'Trickplay', 'Franchises', 'Diagnostics',
        'Subtitles', 'Uploads', 'ProgressSync', 'AniList', 'Devices', 'Registration', 'Translation', 'Ocr', 'SmartPdf',
        'LocalDiscovery', 'Browser'));
