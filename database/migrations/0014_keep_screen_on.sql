-- The profile may keep the screen on while a video plays or a book is read. No baseline edits.
ALTER TABLE "ProfilePlaybackPreferences" ADD COLUMN IF NOT EXISTS "KeepScreenOn" boolean NOT NULL DEFAULT TRUE;
