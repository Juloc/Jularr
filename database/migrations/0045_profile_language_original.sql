-- A profile's language chain is its first and second content language; the original language is allowed unless the profile switches it off.
ALTER TABLE "Profiles" ADD COLUMN IF NOT EXISTS "LanguageUsesOriginal" boolean NOT NULL DEFAULT TRUE;
