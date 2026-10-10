-- Phase B DRAFT 12: preserve one canonical Wanted target per Work/unit and
-- explicit multi-target coverage for a single acquired pack/download.
-- Based on approved docs/AUTOMATIC_RELEASE_SELECTION.md multi-target rules.
-- Run ONLY after parts 01..11 on an isolated disposable scratch database.
-- No separate release scheduler or per-unit duplicate download operation.
BEGIN;

ALTER TABLE "WantedItems"
    ADD CONSTRAINT "UX_WantedItems_ExactTarget" UNIQUE NULLS NOT DISTINCT
        ("WorkId","WorkSeasonId","WorkEpisodeId","WorkVolumeId",
         "WorkChapterId","WorkTrackId","WorkEditionId"),
    ADD CONSTRAINT "UX_WantedItems_Id_WorkId" UNIQUE ("Id","WorkId");

ALTER TABLE "AcquisitionDownloadBindings"
    ADD CONSTRAINT "UX_AcquisitionDownloadBindings_Id_WorkId" UNIQUE ("Id","WorkId");

-- One SABnzbd/direct-import binding may satisfy several confirmed wanted units
-- inside the same canonical Work. Unconfirmed pack contents are not persisted
-- as fulfilled, and no new Work identity or second queue is introduced.
CREATE TABLE "AcquisitionDownloadWantedItems" (
    "AcquisitionDownloadBindingId" bigint NOT NULL,
    "WantedItemId" bigint NOT NULL,
    "WorkId" bigint NOT NULL,
    "ConfirmedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AcquisitionDownloadWantedItems"
        PRIMARY KEY ("AcquisitionDownloadBindingId","WantedItemId"),
    CONSTRAINT "FK_AcquisitionDownloadWantedItems_BindingWork"
        FOREIGN KEY ("AcquisitionDownloadBindingId","WorkId")
        REFERENCES "AcquisitionDownloadBindings" ("Id","WorkId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AcquisitionDownloadWantedItems_TargetWork"
        FOREIGN KEY ("WantedItemId","WorkId")
        REFERENCES "WantedItems" ("Id","WorkId") ON DELETE RESTRICT
);
CREATE INDEX "IX_AcquisitionDownloadWantedItems_Wanted"
    ON "AcquisitionDownloadWantedItems" ("WantedItemId","AcquisitionDownloadBindingId");
COMMIT;
