-- The Owner's Codex choices (AI page): the model and reasoning effort that translate, a model that takes over when it drops out, and Jularr's own token
-- limits. NULL model and effort leave Codex's own default; 0 tokens means unlimited. The warning shows from this percent of the daily limit.
ALTER TABLE "InstanceSettings"
    ADD COLUMN "CodexModel" varchar(64) NULL CHECK ("CodexModel" ~ '^[A-Za-z0-9._:-]{1,64}$'),
    ADD COLUMN "CodexEffort" varchar(16) NULL CHECK ("CodexEffort" ~ '^[a-z]{3,16}$'),
    ADD COLUMN "CodexFallbackModel" varchar(64) NULL CHECK ("CodexFallbackModel" ~ '^[A-Za-z0-9._:-]{1,64}$'),
    ADD COLUMN "CodexDailyTokens" bigint NOT NULL DEFAULT 0 CHECK ("CodexDailyTokens" >= 0),
    ADD COLUMN "CodexRunTokens" bigint NOT NULL DEFAULT 0 CHECK ("CodexRunTokens" >= 0),
    ADD COLUMN "CodexWarnPercent" smallint NOT NULL DEFAULT 80 CHECK ("CodexWarnPercent" BETWEEN 1 AND 100);
