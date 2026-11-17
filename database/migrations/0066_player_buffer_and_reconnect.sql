-- How far a player loads ahead (the server converts that far and a little more), and after how many seconds of a picture that stands still the player asks whether its
-- session is still there; the Owner sets both on the Transcoding page.
ALTER TABLE "TranscodeSettings"
    ADD COLUMN IF NOT EXISTS "BufferSeconds" smallint NOT NULL DEFAULT 120 CHECK ("BufferSeconds" BETWEEN 30 AND 600),
    ADD COLUMN IF NOT EXISTS "ReconnectSeconds" smallint NOT NULL DEFAULT 3 CHECK ("ReconnectSeconds" BETWEEN 1 AND 30);
