-- One stable main profile per account determines the installed PWA's accent.
-- A profile's color belongs to that profile rather than whichever browser is active.
ALTER TABLE "Profiles"
    ADD COLUMN "AccentHex" varchar(7) NOT NULL DEFAULT '#8040f7'
        CHECK ("AccentHex" ~ '^#[0-9a-f]{6}$');

ALTER TABLE "Accounts"
    ADD COLUMN "PwaMainProfileId" bigint NULL;

-- A main profile must remain assigned to the same account, even for shared profiles.
-- Removing that assignment clears the choice instead of leaking another account's appearance.
ALTER TABLE "Accounts"
    ADD CONSTRAINT "FK_Accounts_PwaMainProfile"
    FOREIGN KEY ("Id", "PwaMainProfileId")
    REFERENCES "AccountProfiles" ("AccountId", "ProfileId")
    ON DELETE SET NULL ("PwaMainProfileId");

-- Existing accounts get one deterministic main profile: own profile first, then a shared profile.
UPDATE "Accounts" AS account
SET "PwaMainProfileId" = (
    SELECT profile."Id"
    FROM "AccountProfiles" AS link
    JOIN "Profiles" AS profile ON profile."Id" = link."ProfileId"
    WHERE link."AccountId" = account."Id"
    ORDER BY (profile."OwnerAccountId" = account."Id") DESC, profile."Id"
    LIMIT 1
)
WHERE account."PwaMainProfileId" IS NULL;
