-- Add-ons of a language, for every media type: release groups and words that are preferred (points) or blocked (rejected in an automatic search, marked in a manual one) in releases of that language.
CREATE TABLE "LanguageReleaseRules" (
    "Language" varchar(2) PRIMARY KEY CHECK ("Language" ~ '^[a-z]{2}$'),
    "PreferredGroups" varchar(64)[] NOT NULL DEFAULT '{}' CHECK (cardinality("PreferredGroups") <= 100),
    "BlockedGroups" varchar(64)[] NOT NULL DEFAULT '{}' CHECK (cardinality("BlockedGroups") <= 100),
    "PreferredWords" varchar(64)[] NOT NULL DEFAULT '{}' CHECK (cardinality("PreferredWords") <= 100),
    "BlockedWords" varchar(64)[] NOT NULL DEFAULT '{}' CHECK (cardinality("BlockedWords") <= 100),
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version" > 0)
);
