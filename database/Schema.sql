BEGIN;

CREATE TABLE "DatabaseSchemaVersions" (
    "Version" integer PRIMARY KEY CHECK ("Version" > 0),
    "Name" varchar(200) NOT NULL CHECK ("Name" <> ''),
    "Fingerprint" varchar(64) NOT NULL CHECK ("Fingerprint" ~ '^[0-9a-f]{64}$'),
    "AppliedAt" timestamptz NOT NULL DEFAULT now()
);

COMMIT;
