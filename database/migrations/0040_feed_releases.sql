-- Releases an indexer's recent feed (RSS) has shown, so a restart does not look at them again. Entries leave after 14 days (FeedSync); the table stays small.
CREATE TABLE "FeedReleases" (
    "AcquisitionConnectionId" bigint NOT NULL REFERENCES "AcquisitionConnections" ("Id") ON DELETE CASCADE,
    "ReleaseKey" varchar(600) NOT NULL CHECK (length("ReleaseKey") > 0),
    "SeenAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("AcquisitionConnectionId", "ReleaseKey")
);
CREATE INDEX "IX_FeedReleases_SeenAt" ON "FeedReleases" ("SeenAt");
