-- WorkLibraryRelations: its identity part (Plex or a person's edit) and its classification part (a manual subtype or series)
-- read only the few marked rows instead of every catalog Work and identity.
CREATE INDEX IF NOT EXISTS "IX_WorkExternalIdentities_LibraryTie" ON "WorkExternalIdentities" ("WorkId") WHERE "Namespace" = 'plex' OR "IsManual";
CREATE INDEX IF NOT EXISTS "IX_Works_ManualClass" ON "Works" ("Id") WHERE "IsMediaSubtypeManual" OR "IsFranchiseManual";
