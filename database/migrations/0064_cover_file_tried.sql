-- An album that has no picture in its own audio files is not looked into again for a week.
ALTER TABLE "WorkCovers" ADD COLUMN IF NOT EXISTS "FileTriedAt" timestamptz NULL;
