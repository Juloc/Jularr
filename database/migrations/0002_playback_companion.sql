-- Associate new playback sessions with the actual playback device, never with a transferable bearer.
ALTER TABLE "PlaybackSessions"
    ADD COLUMN "OwnerSessionId" bigint NULL REFERENCES "AccountSessions" ("Id") ON DELETE SET NULL,
    ADD COLUMN "OwnerSessionRevision" bigint NULL;

CREATE INDEX "IX_PlaybackSessions_OwnerSession" ON "PlaybackSessions" ("OwnerSessionId") WHERE "EndedAt" IS NULL;

-- The invitation secret is never stored or logged. The row is single-use and replaceable by its TV owner.
CREATE TABLE "PlaybackCompanionInvitations" (
    "PlaybackSessionId" bigint PRIMARY KEY REFERENCES "PlaybackSessions" ("Id") ON DELETE CASCADE,
    "TokenHash" bytea NOT NULL UNIQUE CHECK (octet_length("TokenHash") = 32),
    "ExpiresAt" timestamptz NOT NULL,
    "ClaimedAt" timestamptz NULL
);

CREATE TABLE "PlaybackCompanionGrants" (
    "Id" uuid PRIMARY KEY,
    "PlaybackSessionId" bigint NOT NULL REFERENCES "PlaybackSessions" ("Id") ON DELETE CASCADE,
    "ControllerSessionId" bigint NOT NULL REFERENCES "AccountSessions" ("Id") ON DELETE CASCADE,
    "ControllerSessionRevision" bigint NOT NULL,
    "RevokedAt" timestamptz NULL,
    UNIQUE ("PlaybackSessionId", "ControllerSessionId")
);

CREATE INDEX "IX_PlaybackCompanionGrants_Controller" ON "PlaybackCompanionGrants" ("ControllerSessionId");

-- Revision is the one serialized control channel for a playing TV. TV state is authoritative.
CREATE TABLE "PlaybackCompanionStates" (
    "PlaybackSessionId" bigint PRIMARY KEY REFERENCES "PlaybackSessions" ("Id") ON DELETE CASCADE,
    "Revision" bigint NOT NULL DEFAULT 0 CHECK ("Revision" >= 0),
    "IsPlaying" boolean NOT NULL DEFAULT FALSE,
    "PositionMs" bigint NOT NULL DEFAULT 0 CHECK ("PositionMs" BETWEEN 0 AND 864000000),
    "DurationMs" bigint NULL CHECK ("DurationMs" BETWEEN 1 AND 864000000),
    "ReportedAt" timestamptz NULL,
    "LastCommandId" uuid NULL,
    "LastCommandSucceeded" boolean NULL
);

CREATE TABLE "PlaybackCompanionCommands" (
    "Id" uuid PRIMARY KEY,
    "PlaybackSessionId" bigint NOT NULL REFERENCES "PlaybackSessions" ("Id") ON DELETE CASCADE,
    "ControllerGrantId" uuid NOT NULL REFERENCES "PlaybackCompanionGrants" ("Id") ON DELETE CASCADE,
    "Revision" bigint NOT NULL CHECK ("Revision" > 0),
    "Action" varchar(8) NOT NULL CHECK ("Action" IN ('play', 'pause', 'seek')),
    "PositionMs" bigint NULL CHECK ("PositionMs" BETWEEN 0 AND 864000000),
    "CreatedAt" timestamptz NOT NULL DEFAULT statement_timestamp(),
    "ExpiresAt" timestamptz NOT NULL DEFAULT (statement_timestamp() + interval '12 seconds'),
    "DeliveredAt" timestamptz NULL,
    "FinishedAt" timestamptz NULL,
    "Succeeded" boolean NULL,
    UNIQUE ("PlaybackSessionId", "Revision"),
    CHECK (("Action" = 'seek') = ("PositionMs" IS NOT NULL))
);

CREATE INDEX "IX_PlaybackCompanionCommands_Next"
    ON "PlaybackCompanionCommands" ("PlaybackSessionId", "Revision") WHERE "DeliveredAt" IS NULL;
