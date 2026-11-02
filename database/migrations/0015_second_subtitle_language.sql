-- A profile may show a second subtitle language above the first. No baseline edits.
ALTER TABLE "ProfilePlaybackPreferences" ADD COLUMN IF NOT EXISTS "SecondSubtitleLanguage" varchar(2) NULL;
