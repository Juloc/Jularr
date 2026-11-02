-- WorkLibraryRelations: its identity part (Plex or a person's edit) and its classification part (a manual subtype or series)
-- read only the few marked rows instead of every catalog Work and identity.
CREATE INDEX IF NOT EXISTS "IX_WorkExternalIdentities_LibraryTie" ON "WorkExternalIdentities" ("WorkId") WHERE "Namespace" = 'plex' OR "IsManual";
CREATE INDEX IF NOT EXISTS "IX_Works_ManualClass" ON "Works" ("Id") WHERE "IsMediaSubtypeManual" OR "IsFranchiseManual";

-- Its request and progress parts are asked for one Work at a time.
CREATE INDEX IF NOT EXISTS "IX_AcquisitionRequests_Work" ON "AcquisitionRequests" ("WorkId");
CREATE INDEX IF NOT EXISTS "IX_MediaProgress_Work" ON "MediaProgress" ("WorkId");

-- The metadata fill reads the Works with details by type; catalog titles without details are not among them.
CREATE INDEX IF NOT EXISTS "IX_Works_Detailed" ON "Works" ("MediaTypeId", "Id") WHERE "DetailedAt" IS NOT NULL;

-- Where the view is read whole (the System page's count, the metadata fill), its parts for a person's edits read only the marked rows.
CREATE INDEX IF NOT EXISTS "IX_WorkMetadataFacts_Manual" ON "WorkMetadataFacts" ("WorkId") WHERE "IsManual";
CREATE INDEX IF NOT EXISTS "IX_WorkRelations_PersonsEdit" ON "WorkRelations" ("WorkId", "RelatedWorkId") WHERE "SourceKey" = 'manual' OR "IsSuppressed";
CREATE INDEX IF NOT EXISTS "IX_WorkUnitExternalIdentities_Manual" ON "WorkUnitExternalIdentities" ("WorkUnitId") WHERE "IsManual";
