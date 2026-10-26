ALTER TABLE "Profiles"
    ADD COLUMN "FirstContentLanguage" varchar(2) NULL CHECK ("FirstContentLanguage" ~ '^[a-z]{2}$'),
    ADD COLUMN "SecondContentLanguage" varchar(2) NULL CHECK ("SecondContentLanguage" ~ '^[a-z]{2}$'),
    ADD CONSTRAINT "CK_Profiles_ContentLanguages" CHECK (
        "SecondContentLanguage" IS NULL
        OR "FirstContentLanguage" IS NOT NULL AND "SecondContentLanguage" <> "FirstContentLanguage"
    );
