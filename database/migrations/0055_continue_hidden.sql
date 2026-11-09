-- A profile can take a title out of its "continue" rows without losing its progress: the row stays, and the title returns when the profile watches or reads it again
-- (every progress write sets UpdatedAt past this mark).
ALTER TABLE "MediaProgress" ADD COLUMN IF NOT EXISTS "ContinueHiddenAt" timestamptz NULL;
