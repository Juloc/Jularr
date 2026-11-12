-- The cause of a failed background job beside its error key (exception type and message, never a secret), on the job and on its notice.
ALTER TABLE "WorkerJobs" ADD COLUMN "LastErrorDetail" varchar(300) NULL;
ALTER TABLE "WorkerJobs" ADD CONSTRAINT "WorkerJobs_LastErrorDetail_check" CHECK ("LastErrorDetail" IS NULL OR "LastErrorKey" IS NOT NULL);
ALTER TABLE "Operations" ADD COLUMN "ErrorDetail" varchar(300) NULL;
ALTER TABLE "Operations" ADD CONSTRAINT "Operations_ErrorDetail_check" CHECK ("ErrorDetail" IS NULL OR "ErrorKey" IS NOT NULL);

-- The cover job walks only the provider addresses still to fetch, a bounded window at a time, and only the books of the library.
CREATE INDEX IF NOT EXISTS "IX_WorkMetadataFacts_RemoteImage" ON "WorkMetadataFacts" ("Id") WHERE "FactKey" IN ('Poster', 'Backdrop') AND "Value" ~ '^https?://';
CREATE INDEX IF NOT EXISTS "IX_WorkUnits_RemoteImage" ON "WorkUnits" ("Id") WHERE "ImageUrl" ~ '^https?://';
CREATE INDEX IF NOT EXISTS "IX_MediaAssets_Book" ON "MediaAssets" ("WorkId") WHERE "MediaAssetTypeId" IN (3, 4);
