-- Preserve existing import behaviour until a media type's settings are changed.
CREATE TABLE "MediaManagementSettings" (
    "MediaTypeId" smallint PRIMARY KEY CHECK ("MediaTypeId" IN (1, 2, 3, 6, 7)),
    "UsesHardlinks" boolean NOT NULL DEFAULT TRUE,
    "SkipsFreeSpaceCheck" boolean NOT NULL DEFAULT FALSE,
    "MinimumFreeSpaceMegabytes" bigint NOT NULL DEFAULT 0 CHECK ("MinimumFreeSpaceMegabytes" BETWEEN 0 AND 2147483647),
    "AnalysesMedia" boolean NOT NULL DEFAULT TRUE,
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version" > 0)
);
INSERT INTO "MediaManagementSettings" ("MediaTypeId") VALUES (1), (2), (3), (6), (7)
ON CONFLICT ("MediaTypeId") DO NOTHING;
