BEGIN;

CREATE TABLE "StorageProtocolTypes" (
    "Id" smallint PRIMARY KEY CHECK ("Id" BETWEEN 1 AND 3),
    "Key" varchar(16) NOT NULL UNIQUE
);

INSERT INTO "StorageProtocolTypes" ("Id", "Key") VALUES (1, 'Smb'), (2, 'Nfs'), (3, 'Local');

CREATE TABLE "StorageProtocolVersions" (
    "StorageProtocolTypeId" smallint NOT NULL REFERENCES "StorageProtocolTypes" ("Id"),
    "ProtocolVersion" varchar(8) NOT NULL,
    PRIMARY KEY ("StorageProtocolTypeId", "ProtocolVersion")
);

INSERT INTO "StorageProtocolVersions" ("StorageProtocolTypeId", "ProtocolVersion") VALUES (1, '3.1.1'), (1, '3.0'), (2, '4.1'), (2, '4.2');

-- Local connections are permitted host or container paths.
CREATE TABLE "StorageMounts" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Name" varchar(128) NOT NULL CHECK (length(btrim("Name")) > 0),
    "StorageProtocolTypeId" smallint NOT NULL REFERENCES "StorageProtocolTypes" ("Id"),
    "Host" varchar(253) NULL CHECK (length("Host") > 0),
    "SharePath" varchar(1024) NOT NULL CHECK (length("SharePath") > 0),
    "ProtocolVersion" varchar(8) NULL,
    "Username" varchar(256) NULL,
    "Domain" varchar(256) NULL,
    "ProtectedPassword" bytea NULL,
    "IsReadOnly" boolean NOT NULL,
    "WakeMacAddress" varchar(17) NULL CHECK ("WakeMacAddress" ~ '^([0-9A-F]{2}:){5}[0-9A-F]{2}$'),
    "WakeBroadcastAddress" varchar(15) NULL,
    "WakeOnDemand" boolean NOT NULL DEFAULT FALSE,
    "SynologyUrl" varchar(2048) NULL CHECK ("SynologyUrl" ~ '^https?://'),
    "SynologyUsername" varchar(256) NULL CHECK (length("SynologyUsername") > 0),
    "SynologyProtectedPassword" bytea NULL,
    -- A self-signed DSM certificate the Owner confirmed once (SHA-256 of the certificate); a new address forgets it.
    "SynologyCertificateSha256" varchar(64) NULL CHECK ("SynologyCertificateSha256" ~ '^[0-9A-F]{64}$'),
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version" > 0),
    "CreatedAt" timestamptz NOT NULL DEFAULT statement_timestamp(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT statement_timestamp(),
    FOREIGN KEY ("StorageProtocolTypeId", "ProtocolVersion") REFERENCES "StorageProtocolVersions" ("StorageProtocolTypeId", "ProtocolVersion"),
    CHECK (
        ("StorageProtocolTypeId" = 1 AND "SharePath" LIKE '/%' AND "Host" IS NOT NULL AND "ProtocolVersion" IS NOT NULL AND length("Username") > 0 AND "Username" IS NOT NULL)
        OR ("StorageProtocolTypeId" = 2 AND "SharePath" LIKE '/%' AND "Host" IS NOT NULL AND "ProtocolVersion" IS NOT NULL AND "Username" IS NULL AND "Domain" IS NULL AND "ProtectedPassword" IS NULL)
        OR ("StorageProtocolTypeId" = 3 AND "Host" IS NULL AND "ProtocolVersion" IS NULL AND "Username" IS NULL AND "Domain" IS NULL AND "ProtectedPassword" IS NULL)
    ),
    CHECK ("WakeMacAddress" IS NOT NULL OR ("WakeBroadcastAddress" IS NULL AND NOT "WakeOnDemand")),
    CHECK (("SynologyUrl" IS NULL) = ("SynologyUsername" IS NULL) AND ("SynologyUrl" IS NULL) = ("SynologyProtectedPassword" IS NULL))
);
CREATE UNIQUE INDEX "UX_StorageMounts_LocalPath" ON "StorageMounts" ("SharePath") WHERE "StorageProtocolTypeId" = 3;

-- Last observed state and the Owner's connect wish.
CREATE TABLE "StorageAvailabilityTypes" (
    "Id" smallint PRIMARY KEY,
    "Key" varchar(32) NOT NULL UNIQUE
);
INSERT INTO "StorageAvailabilityTypes" ("Id", "Key") VALUES (1, 'Available'), (2, 'Offline'), (3, 'AccessDenied');

CREATE TABLE "StorageMountStates" (
    "StorageMountId" bigint PRIMARY KEY REFERENCES "StorageMounts" ("Id") ON DELETE CASCADE,
    "IsConnectRequested" boolean NOT NULL DEFAULT FALSE,
    "StorageAvailabilityTypeId" smallint NULL REFERENCES "StorageAvailabilityTypes" ("Id"),
    "DiagnosticCode" varchar(64) NULL,
    "CheckedAt" timestamptz NULL,
    "AvailableAt" timestamptz NULL,
    "FreeBytes" bigint NULL CHECK ("FreeBytes" >= 0),
    "TotalBytes" bigint NULL CHECK ("TotalBytes" >= 0),
    "ReconnectAttempts" integer NOT NULL DEFAULT 0 CHECK ("ReconnectAttempts" >= 0),
    "ReconnectAt" timestamptz NULL,
    CHECK (("StorageAvailabilityTypeId" IS NULL) = ("CheckedAt" IS NULL))
);
CREATE INDEX "IX_StorageMountStates_Reconnect" ON "StorageMountStates" ("ReconnectAt") WHERE "IsConnectRequested";

-- Offline with Wake-on-LAN reads as asleep.
CREATE VIEW "StorageMountHealth" AS
SELECT
    mount."Id" AS "StorageMountId",
    CASE state."StorageAvailabilityTypeId"
        WHEN 1 THEN 1
        WHEN 3 THEN 4
        WHEN 2 THEN CASE WHEN mount."WakeMacAddress" IS NOT NULL THEN 2 ELSE 3 END
    END::smallint AS "StorageHealthTypeId",
    state."DiagnosticCode",
    state."CheckedAt",
    state."FreeBytes",
    state."TotalBytes"
FROM
    "StorageMounts" AS mount
    LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id";

-- A connection holding the recycle folder cannot go while it is configured.
ALTER TABLE "InstanceSettings" ADD FOREIGN KEY ("RecycleStorageMountId") REFERENCES "StorageMounts" ("Id") ON DELETE RESTRICT;

COMMIT;
