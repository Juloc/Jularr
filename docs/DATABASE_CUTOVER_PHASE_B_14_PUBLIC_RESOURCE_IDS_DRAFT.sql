-- PHASE B DRAFT 14: public/resource IDs are NOT internal relational bigint IDs.
-- Explicit owner clarification (2026-10-10); issue #880 and #852.
-- Internal Id bigint/FK remains for joins. Only independently visible resources
-- receive PublicId; fixed lookup, junction and transient technical rows do not.
-- PublicId is an addressable resource identifier, NOT a session/access token,
-- credential, proof of authorization, provider external ID or raw secret.
-- Apply after 01..13 on an EMPTY ISOLATED PostgreSQL scratch database.
BEGIN;

ALTER TABLE "Accounts"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Accounts_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Profiles"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Profiles_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Works"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Works_PublicId" UNIQUE ("PublicId");

ALTER TABLE "WorkEpisodes"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_WorkEpisodes_PublicId" UNIQUE ("PublicId");

ALTER TABLE "WorkChapters"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_WorkChapters_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Images"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Images_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Collections"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Collections_PublicId" UNIQUE ("PublicId");

ALTER TABLE "AcquisitionRequests"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_AcquisitionRequests_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Operations"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Operations_PublicId" UNIQUE ("PublicId");

ALTER TABLE "PlaybackSessions"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_PlaybackSessions_PublicId" UNIQUE ("PublicId");

ALTER TABLE "ReaderBookmarks"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_ReaderBookmarks_PublicId" UNIQUE ("PublicId");

ALTER TABLE "ReaderHighlights"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_ReaderHighlights_PublicId" UNIQUE ("PublicId");

ALTER TABLE "LearningCourses"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_LearningCourses_PublicId" UNIQUE ("PublicId");

ALTER TABLE "LearningCards"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_LearningCards_PublicId" UNIQUE ("PublicId");

ALTER TABLE "Notifications"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_Notifications_PublicId" UNIQUE ("PublicId");

ALTER TABLE "LibraryRoots"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD CONSTRAINT "UX_LibraryRoots_PublicId" UNIQUE ("PublicId");

-- Session/refresh/reset/invite/link/capability credentials must remain separately
-- generated cryptographic secrets. Persist only purpose-appropriate token HASH
-- or encrypted secret/secure reference, never the bearer token in a bigint column.
-- User/Profile/Work visibility requires current server-side role/scope permission
-- checks BEFORE lookup/mutation; knowing a PublicId grants no access.
COMMIT;
