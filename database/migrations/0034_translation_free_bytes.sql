-- The free space of Ollama's own model volume, which only the Owner can know: typed on the translation page instead of an environment variable. 0 falls back to
-- `LocalTranslation:OperatorVerifiedFreeBytes`, and with both at 0 no model download is offered.
ALTER TABLE "InstanceSettings" ADD COLUMN "TranslationFreeBytes" bigint NOT NULL DEFAULT 0 CHECK ("TranslationFreeBytes" >= 0);
