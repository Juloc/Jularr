-- AniList entries of anime and where they lie at TheTVDB, from the AniBridge mappings (anibridge/anibridge-mappings, MIT), loaded daily.
-- One row per entry and TheTVDB season: AniListEpisode is the entry's first episode in that season, FirstEpisode and LastEpisode the season's
-- episodes it covers (no last: still running). A load replaces all rows at once; a failed or odd download leaves them as they are.
CREATE TABLE "AnimeMappings" (
    "AniListId" integer NOT NULL CHECK ("AniListId" > 0),
    "TvdbId" integer NOT NULL CHECK ("TvdbId" > 0),
    "SeasonNumber" smallint NOT NULL CHECK ("SeasonNumber" >= 0),
    "AniListEpisode" integer NOT NULL CHECK ("AniListEpisode" > 0),
    "FirstEpisode" integer NOT NULL CHECK ("FirstEpisode" > 0),
    "LastEpisode" integer NULL CHECK ("LastEpisode" >= "FirstEpisode"),
    PRIMARY KEY ("AniListId", "TvdbId", "SeasonNumber")
);
CREATE INDEX "IX_AnimeMappings_Tvdb" ON "AnimeMappings" ("TvdbId");

-- The loaded release of the mappings: its ETag for the next conditional download, its schema and when it was made, checked and loaded.
CREATE TABLE "AnimeMappingSources" (
    "Id" boolean PRIMARY KEY DEFAULT TRUE CHECK ("Id"),
    "ETag" varchar(200) NULL,
    "SchemaVersion" varchar(32) NULL,
    "GeneratedAt" timestamptz NULL,
    "CheckedAt" timestamptz NULL,
    "LoadedAt" timestamptz NULL
);
INSERT INTO "AnimeMappingSources" ("Id") VALUES (TRUE);

-- TheTVDB has no full list: its catalog card only budgets the details of Discover's series lists (requests a minute, pause while used, windows).
INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute", "PausesWhileUsed") VALUES ('tvdb', 120, FALSE) ON CONFLICT ("Namespace") DO NOTHING;
