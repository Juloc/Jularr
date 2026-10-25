-- An explicit profile opt-in; viewing or simply favoriting never downloads by default.
-- The Requests module and ordinary request approval/rate rules still govern automatic requests.
ALTER TABLE "Profiles"
    ADD COLUMN IF NOT EXISTS "AutoRequestOnFavorite" boolean NOT NULL DEFAULT FALSE;
