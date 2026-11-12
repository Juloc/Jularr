-- Where searches saw a release group: one row per media type, counted like the group itself. Groups seen before this migration have none until their next sighting.
-- The rating stays on the group; a rating per media type can join this table later.
CREATE TABLE "ReleaseGroupMediaKinds" (
    "ReleaseGroupId" bigint NOT NULL REFERENCES "ReleaseGroups" ("Id") ON DELETE CASCADE,
    "AcquisitionMediaKindId" smallint NOT NULL REFERENCES "AcquisitionMediaKinds" ("Id"),
    "SeenCount" integer NOT NULL DEFAULT 0 CHECK ("SeenCount" >= 0),
    "LastSeenAt" timestamptz NULL,
    PRIMARY KEY ("ReleaseGroupId", "AcquisitionMediaKindId")
);
CREATE INDEX "IX_ReleaseGroupMediaKinds_Kind" ON "ReleaseGroupMediaKinds" ("AcquisitionMediaKindId", "ReleaseGroupId");
