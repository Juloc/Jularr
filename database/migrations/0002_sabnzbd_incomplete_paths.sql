-- SABnzbd keeps its incomplete workspace separately from each category's complete folder.
-- Existing installations retain all connection settings; unset mappings are non-destructive.
ALTER TABLE "AcquisitionConnections"
    ADD COLUMN IF NOT EXISTS "IncompleteRemotePath" varchar(1024) NULL,
    ADD COLUMN IF NOT EXISTS "IncompleteLocalPath" varchar(1024) NULL;

ALTER TABLE "AcquisitionConnections"
    ADD CONSTRAINT "AcquisitionConnections_IncompletePathsPair"
    CHECK (("IncompleteRemotePath" IS NULL) = ("IncompleteLocalPath" IS NULL));
