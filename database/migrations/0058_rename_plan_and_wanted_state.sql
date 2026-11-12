-- The library rename plan, built by the worker after scans, downloads and naming or assignment changes, at the latest an hour after the last build, and
-- on the Owner's request, instead of on every visit of the Rename tab. One plan for all libraries; its rows in the order the tab lists them.
-- Applying builds the plan again and refuses it when it differs from the one shown. Empty until the first build.
CREATE TABLE "LibraryRenamePlans" (
    "Id" smallint PRIMARY KEY CHECK ("Id" = 1),
    "Fingerprint" varchar(64) NOT NULL,
    "RenameCount" integer NOT NULL CHECK ("RenameCount" >= 0),
    "BuiltAt" timestamptz NOT NULL,
    -- Something the plan reads changed since; the worker builds it again.
    "RequestedAt" timestamptz NULL
);

-- A copy of the plan at "BuiltAt": a file renamed or deleted since keeps its row until the next build.
CREATE TABLE "LibraryRenamePlanItems" (
    "Position" integer PRIMARY KEY CHECK ("Position" > 0),
    "StoredFileId" bigint NOT NULL,
    "MediaStoredFileId" bigint NOT NULL,
    "LibraryRootId" bigint NOT NULL,
    "LibraryRootName" varchar(128) NOT NULL,
    "RelativePath" text NOT NULL,
    "TargetPath" text NOT NULL
);
