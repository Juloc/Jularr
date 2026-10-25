-- Media list imports share Operations' claim/recovery/history lifecycle. No baseline edits.
INSERT INTO "OperationTypes" ("Id", "Key") VALUES (6, 'MediaImport');

CREATE UNIQUE INDEX "UX_Operations_ActiveMediaImport" ON "Operations" ("RequestedByAccountId")
WHERE "OperationTypeId" = 6 AND "OperationStatusTypeId" IN (1, 2);

CREATE TABLE "MediaImportJobs" (
    "OperationId" bigint PRIMARY KEY REFERENCES "Operations" ("Id") ON DELETE CASCADE,
    "PublicId" uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
    "RequestedByAccountId" bigint NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
    "Payload" jsonb NOT NULL,
    "TotalCount" integer NOT NULL CHECK ("TotalCount" BETWEEN 1 AND 2000),
    "Cursor" integer NOT NULL DEFAULT 0 CHECK ("Cursor" BETWEEN 0 AND 2000),
    "Sequence" bigint NOT NULL DEFAULT 0 CHECK ("Sequence" >= 0),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CHECK ("Cursor" <= "TotalCount")
);

CREATE TABLE "MediaImportJobRows" (
    "OperationId" bigint NOT NULL REFERENCES "MediaImportJobs" ("OperationId") ON DELETE CASCADE,
    "RowIndex" integer NOT NULL CHECK ("RowIndex" BETWEEN 0 AND 1999),
    "Stage" varchar(16) NOT NULL DEFAULT 'Queued' CHECK ("Stage" IN ('Queued', 'Matching', 'Searching', 'Importing', 'Done')),
    "OutcomeTypeId" smallint NULL CHECK ("OutcomeTypeId" BETWEEN 1 AND 11),
    "Title" varchar(500) NULL,
    "WorkId" bigint NULL REFERENCES "Works" ("Id") ON DELETE SET NULL,
    "Searches" boolean NOT NULL DEFAULT false,
    "Source" varchar(48) NULL,
    "Query" varchar(500) NULL,
    "Detail" varchar(64) NULL,
    "CandidateCount" integer NULL CHECK ("CandidateCount" >= 0),
    "Attempt" integer NOT NULL DEFAULT 0 CHECK ("Attempt" >= 0),
    "History" jsonb NOT NULL DEFAULT '[]'::jsonb,
    "UpdatedAt" timestamptz NULL,
    PRIMARY KEY ("OperationId", "RowIndex")
);
CREATE INDEX "IX_MediaImportJobs_Owner" ON "MediaImportJobs" ("RequestedByAccountId", "OperationId" DESC);
