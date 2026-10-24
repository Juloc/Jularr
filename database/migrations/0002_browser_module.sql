-- Optional browser for acquisition sources; existing installs stay disabled until the Owner enables it.
ALTER TABLE "InstanceModules" DROP CONSTRAINT "InstanceModules_Key_check";
ALTER TABLE "InstanceModules" ADD CONSTRAINT "InstanceModules_Key_check"
CHECK ("Key" IN ('Library', 'Requests', 'Acquisition', 'Playback', 'Reading', 'Profiles', 'Emulation', 'Notifications', 'Collections', 'Calendar',
    'Statistics', 'UnitGroups', 'HomeLayout', 'Trickplay', 'Franchises', 'Diagnostics', 'Subtitles', 'Uploads', 'ProgressSync', 'AniList',
    'Devices', 'Registration', 'Translation', 'Ocr', 'SmartPdf', 'DiscoverCache', 'LocalDiscovery', 'Browser'));
INSERT INTO "InstanceModules" ("Key", "IsEnabled") VALUES ('Browser', FALSE) ON CONFLICT ("Key") DO NOTHING;
INSERT INTO "InstanceModuleDependencies" ("ModuleKey", "RequiredKey") VALUES ('Browser', 'Acquisition') ON CONFLICT DO NOTHING;
