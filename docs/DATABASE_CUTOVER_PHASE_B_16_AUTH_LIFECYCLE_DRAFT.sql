BEGIN;

CREATE TABLE "AuthChallengePurposeTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AuthChallengePurposeTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AuthChallengePurposeTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AuthChallengePurposeTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AuthChallengePurposeTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AuthChallengePurposeTypes" ("Id", "Key")
VALUES
    (1, 'email_verification'),
    (2, 'password_reset'),
    (3, 'totp_enrollment'),
    (4, 'totp_sign_in'),
    (5, 'passkey_enrollment'),
    (6, 'passkey_sign_in'),
    (7, 'external_login'),
    (8, 'external_link'),
    (9, 'provider_media_consent');

ALTER TABLE "Accounts"
    ADD COLUMN "CredentialRevision" bigint NOT NULL DEFAULT 1,
    ADD CONSTRAINT "CK_Accounts_CredentialRevision" CHECK ("CredentialRevision" >= 1);

ALTER TABLE "AccountSessions"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN "CredentialRevision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "RotationRevision" bigint NOT NULL DEFAULT 1,
    ADD COLUMN "RotatedAt" timestamptz,
    ADD CONSTRAINT "UX_AccountSessions_PublicId" UNIQUE ("PublicId"),
    ADD CONSTRAINT "CK_AccountSessions_Hash" CHECK (octet_length("TokenHash") = 32),
    ADD CONSTRAINT "CK_AccountSessions_Revisions" CHECK ("CredentialRevision" >= 1 AND "RotationRevision" >= 1),
    ADD CONSTRAINT "CK_AccountSessions_Timestamps" CHECK (
        ("RevokedAt" IS NULL OR "RevokedAt" >= "CreatedAt")
        AND ("LastSeenAt" IS NULL OR "LastSeenAt" >= "CreatedAt")
        AND ("RotatedAt" IS NULL OR "RotatedAt" >= "CreatedAt"));
CREATE INDEX "IX_AccountSessions_Account_Created" ON "AccountSessions" ("AccountId", "CreatedAt" DESC, "Id" DESC);
CREATE INDEX "IX_AccountSessions_Live_Expiry" ON "AccountSessions" ("ExpiresAt", "Id") WHERE "RevokedAt" IS NULL;

ALTER TABLE "AccountAuthChallenges"
    DROP COLUMN "ChallengeKey",
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN "AuthChallengePurposeTypeId" smallint NOT NULL,
    ADD COLUMN "CredentialRevision" bigint,
    ADD COLUMN "ProtectedState" bytea,
    ADD CONSTRAINT "UX_AccountAuthChallenges_PublicId" UNIQUE ("PublicId"),
    ADD CONSTRAINT "UX_AccountAuthChallenges_Id_Purpose" UNIQUE ("Id", "AuthChallengePurposeTypeId"),
    ADD CONSTRAINT "FK_AccountAuthChallenges_Purpose" FOREIGN KEY ("AuthChallengePurposeTypeId")
        REFERENCES "AuthChallengePurposeTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_AccountAuthChallenges_Account" CHECK (
        ("AccountId" IS NULL AND "CredentialRevision" IS NULL AND "AuthChallengePurposeTypeId" IN (6, 7))
        OR ("AccountId" IS NOT NULL AND "CredentialRevision" IS NOT NULL AND "CredentialRevision" >= 1)),
    ADD CONSTRAINT "CK_AccountAuthChallenges_Hash" CHECK (octet_length("TokenHash") = 32),
    ADD CONSTRAINT "CK_AccountAuthChallenges_State" CHECK (
        "ProtectedState" IS NULL OR octet_length("ProtectedState") BETWEEN 1 AND 16384),
    ADD CONSTRAINT "CK_AccountAuthChallenges_Consumed" CHECK ("ConsumedAt" IS NULL OR "ConsumedAt" >= "CreatedAt");
CREATE INDEX "IX_AccountAuthChallenges_Account" ON "AccountAuthChallenges" ("AccountId", "Id") WHERE "ConsumedAt" IS NULL;
CREATE INDEX "IX_AccountAuthChallenges_Expiry" ON "AccountAuthChallenges" ("ExpiresAt", "Id");

CREATE TABLE "AccountExternalAuthFlows" (
    "AccountAuthChallengeId" bigint NOT NULL,
    "AuthChallengePurposeTypeId" smallint NOT NULL,
    "ProviderId" bigint NOT NULL,
    "StartedProfileId" bigint,
    "ExternalPinId" bigint NOT NULL,
    "ClientIdentifier" varchar(200) NOT NULL,
    "VerifiedExternalAccountId" varchar(200),
    "ReturnPath" varchar(2048) NOT NULL DEFAULT '/',
    CONSTRAINT "PK_AccountExternalAuthFlows" PRIMARY KEY ("AccountAuthChallengeId"),
    CONSTRAINT "FK_AccountExternalAuthFlows_ChallengePurpose" FOREIGN KEY ("AccountAuthChallengeId", "AuthChallengePurposeTypeId")
        REFERENCES "AccountAuthChallenges" ("Id", "AuthChallengePurposeTypeId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AccountExternalAuthFlows_Providers" FOREIGN KEY ("ProviderId") REFERENCES "Providers" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AccountExternalAuthFlows_Profiles" FOREIGN KEY ("StartedProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_AccountExternalAuthFlows_Purpose" CHECK (
        ("AuthChallengePurposeTypeId" = 9 AND "StartedProfileId" IS NOT NULL)
        OR ("AuthChallengePurposeTypeId" IN (7, 8) AND "StartedProfileId" IS NULL)),
    CONSTRAINT "CK_AccountExternalAuthFlows_Pin" CHECK ("ExternalPinId" > 0),
    CONSTRAINT "CK_AccountExternalAuthFlows_Client" CHECK (
        length("ClientIdentifier") > 0 AND "ClientIdentifier" = btrim("ClientIdentifier")),
    CONSTRAINT "CK_AccountExternalAuthFlows_Verified" CHECK (
        "VerifiedExternalAccountId" IS NULL OR (length("VerifiedExternalAccountId") > 0
        AND "VerifiedExternalAccountId" = btrim("VerifiedExternalAccountId"))),
    CONSTRAINT "CK_AccountExternalAuthFlows_ReturnPath" CHECK (
        "ReturnPath" LIKE '/%' AND "ReturnPath" NOT LIKE '//%'
        AND position(chr(92) IN "ReturnPath") = 0 AND "ReturnPath" !~ '[[:cntrl:]]')
);
CREATE INDEX "IX_AccountExternalAuthFlows_Provider" ON "AccountExternalAuthFlows" ("ProviderId", "AccountAuthChallengeId");
CREATE INDEX "IX_AccountExternalAuthFlows_Profile" ON "AccountExternalAuthFlows" ("StartedProfileId") WHERE "StartedProfileId" IS NOT NULL;

ALTER TABLE "AccountTotpFactors"
    ADD COLUMN "LastAcceptedStep" bigint NOT NULL DEFAULT -1,
    ADD COLUMN "RevokedAt" timestamptz,
    ADD CONSTRAINT "CK_AccountTotpFactors_Secret" CHECK (octet_length("EncryptedSecret") BETWEEN 1 AND 16384),
    ADD CONSTRAINT "CK_AccountTotpFactors_Step" CHECK ("LastAcceptedStep" >= -1),
    ADD CONSTRAINT "CK_AccountTotpFactors_Timestamps" CHECK (
        ("ConfirmedAt" IS NULL OR "ConfirmedAt" >= "CreatedAt")
        AND ("RevokedAt" IS NULL OR "RevokedAt" >= "CreatedAt")
        AND ("LastAcceptedStep" = -1 OR "ConfirmedAt" IS NOT NULL));
CREATE INDEX "IX_AccountTotpFactors_Account" ON "AccountTotpFactors" ("AccountId", "Id");

ALTER TABLE "AccountRecoveryCodes"
    ADD CONSTRAINT "CK_AccountRecoveryCodes_Hash" CHECK (octet_length("CodeHash") = 32),
    ADD CONSTRAINT "CK_AccountRecoveryCodes_Used" CHECK ("UsedAt" IS NULL OR "UsedAt" >= "CreatedAt");

ALTER TABLE "AccountPasskeys"
    ADD COLUMN "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN "DisplayName" varchar(100) NOT NULL DEFAULT 'Passkey',
    ADD COLUMN "RevokedAt" timestamptz,
    ADD CONSTRAINT "UX_AccountPasskeys_PublicId" UNIQUE ("PublicId"),
    ADD CONSTRAINT "CK_AccountPasskeys_Credential" CHECK (octet_length("CredentialId") BETWEEN 1 AND 1024),
    ADD CONSTRAINT "CK_AccountPasskeys_PublicKey" CHECK (octet_length("PublicKey") BETWEEN 1 AND 16384),
    ADD CONSTRAINT "CK_AccountPasskeys_Name" CHECK (length(btrim("DisplayName")) > 0),
    ADD CONSTRAINT "CK_AccountPasskeys_Timestamps" CHECK (
        ("LastUsedAt" IS NULL OR "LastUsedAt" >= "CreatedAt")
        AND ("RevokedAt" IS NULL OR "RevokedAt" >= "CreatedAt"));

ALTER TABLE "AccountPasswords"
    ADD CONSTRAINT "CK_AccountPasswords_Hash" CHECK (length(btrim("PasswordHash")) > 0);
ALTER TABLE "AccountExternalLogins"
    ADD CONSTRAINT "UX_AccountExternalLogins_Account_Provider" UNIQUE ("AccountId", "ProviderId"),
    ADD CONSTRAINT "CK_AccountExternalLogins_Identity" CHECK (
        length("ExternalAccountId") BETWEEN 1 AND 200 AND "ExternalAccountId" = btrim("ExternalAccountId"));

COMMIT;
