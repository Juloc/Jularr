-- Preserve an immutable digest of the imported source listing before any automatic
-- post-import cleanup. Existing unverified cleanup rows are deliberately not authorized
-- to remove completed source directories; failed-job cleanup is unchanged.
ALTER TABLE "DownloadClientCleanups"
    ADD COLUMN "SourceFingerprint" char(64) NULL;
