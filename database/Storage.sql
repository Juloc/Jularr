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

-- Local connections are permitted container paths.
CREATE TABLE "StorageMounts" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Name" varchar(128) NOT NULL CHECK (length(btrim("Name")) > 0),
    "StorageProtocolTypeId" smallint NOT NULL REFERENCES "StorageProtocolTypes" ("Id"),
    "Host" varchar(253) NULL CHECK (length("Host") > 0),
    "SharePath" varchar(1024) NOT NULL CHECK ("SharePath" LIKE '/%'),
    "ProtocolVersion" varchar(8) NULL,
    "Username" varchar(256) NULL,
    "Domain" varchar(256) NULL,
    "ProtectedPassword" bytea NULL,
    "IsReadOnly" boolean NOT NULL,
    "WakeMacAddress" varchar(17) NULL CHECK ("WakeMacAddress" ~ '^([0-9A-F]{2}:){5}[0-9A-F]{2}$'),
    "WakeBroadcastAddress" varchar(15) NULL,
    "WakeOnDemand" boolean NOT NULL DEFAULT FALSE,
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version" > 0),
    "CreatedAt" timestamptz NOT NULL DEFAULT statement_timestamp(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT statement_timestamp(),
    FOREIGN KEY ("StorageProtocolTypeId", "ProtocolVersion") REFERENCES "StorageProtocolVersions" ("StorageProtocolTypeId", "ProtocolVersion"),
    CHECK (
        ("StorageProtocolTypeId" = 1 AND "Host" IS NOT NULL AND "ProtocolVersion" IS NOT NULL AND length("Username") > 0 AND "Username" IS NOT NULL)
        OR ("StorageProtocolTypeId" = 2 AND "Host" IS NOT NULL AND "ProtocolVersion" IS NOT NULL AND "Username" IS NULL AND "Domain" IS NULL AND "ProtectedPassword" IS NULL)
        OR ("StorageProtocolTypeId" = 3 AND "Host" IS NULL AND "ProtocolVersion" IS NULL AND "Username" IS NULL AND "Domain" IS NULL AND "ProtectedPassword" IS NULL)
    ),
    CHECK ("WakeMacAddress" IS NOT NULL OR ("WakeBroadcastAddress" IS NULL AND NOT "WakeOnDemand"))
);
CREATE UNIQUE INDEX "UX_StorageMounts_LocalPath" ON "StorageMounts" ("SharePath") WHERE "StorageProtocolTypeId" = 3;

COMMIT;
