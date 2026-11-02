-- Mozilla's browser translation as an optional module for emergencies: off by default, it steps in only when the server's TranslateGemma cannot translate a page of a book.
ALTER TABLE "InstanceModules" DROP CONSTRAINT "InstanceModules_Key_check";
ALTER TABLE "InstanceModules" ADD CONSTRAINT "InstanceModules_Key_check"
    CHECK ("Key" IN ('Library', 'Requests', 'Acquisition', 'Playback', 'Reading', 'Profiles', 'Emulation', 'Notifications',
        'Collections', 'Calendar', 'Statistics', 'UnitGroups', 'HomeLayout', 'Trickplay', 'Franchises', 'Diagnostics',
        'Subtitles', 'Uploads', 'ProgressSync', 'AniList', 'Devices', 'Registration', 'Translation', 'Ocr', 'SmartPdf',
        'DiscoverCache', 'LocalDiscovery', 'Browser', 'Vpn', 'Torrent', 'TranslationBrowser'));
INSERT INTO "InstanceModules" ("Key", "IsEnabled") VALUES ('TranslationBrowser', FALSE)
ON CONFLICT ("Key") DO NOTHING;
INSERT INTO "InstanceModuleDependencies" ("ModuleKey", "RequiredKey") VALUES ('TranslationBrowser', 'Translation')
ON CONFLICT DO NOTHING;
