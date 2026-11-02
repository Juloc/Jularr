-- A Work page lists the other titles of its franchise; without an index that read scans every catalog Work.
CREATE INDEX IF NOT EXISTS "IX_Works_Franchise" ON "Works" ("FranchiseId") WHERE "FranchiseId" IS NOT NULL;

-- Episodes, downloads, markers, wanted and the calendar look up the file of one unit; the unique index leads with the version, so they read every file record.
CREATE INDEX IF NOT EXISTS "IX_MediaAssets_WorkUnit" ON "MediaAssets" ("WorkUnitId") WHERE "WorkUnitId" IS NOT NULL;
