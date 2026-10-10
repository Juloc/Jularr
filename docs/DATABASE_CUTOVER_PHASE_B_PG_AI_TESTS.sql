-- Phase B: source-owned AI routing and usage; isolated scratch-only fixtures.
BEGIN;
DO $phase_b_ai$
DECLARE
    locale_id bigint;
    account_id bigint;
    profile_id bigint;
    provider_id bigint;
    model_a bigint;
    model_b bigint;
BEGIN
    INSERT INTO "UiLocales" ("Locale","Name")
    VALUES ('phase-b-ai','Phase B AI') RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-ai@example.invalid','Phase B AI Account',1)
    RETURNING "Id" INTO account_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (account_id,'Phase B AI Profile',locale_id)
    RETURNING "Id" INTO profile_id;
    INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
    VALUES (account_id,profile_id);

    INSERT INTO "AiServerProviders" ("Key","DisplayName","AdapterKey","BaseUrl","IsEnabled","Priority")
    VALUES ('phase-b-ai-server','AI CI Fixture','openai-compatible','https://ai.example.invalid',true,2)
    RETURNING "Id" INTO provider_id;
    INSERT INTO "AiModels" ("AiServerProviderId","NativeModelId","IsEnabled","Capabilities")
    VALUES (provider_id,'model-A',true,'{"vision":true}'::jsonb)
    RETURNING "Id" INTO model_a;
    INSERT INTO "AiModels" ("AiServerProviderId","NativeModelId","IsEnabled")
    VALUES (provider_id,'model-B',true)
    RETURNING "Id" INTO model_b;
    INSERT INTO "AiTaskRoutes"
      ("TaskKey","PrimaryAiModelId","FallbackAiModelId","IsEnabled")
    VALUES ('novel-translation',model_a,model_b,true);

    BEGIN
        INSERT INTO "AiTaskRoutes"
          ("TaskKey","PrimaryAiModelId","FallbackAiModelId","IsEnabled")
        VALUES ('forbidden-same-model',model_a,model_a,true);
        RAISE EXCEPTION 'Same AI primary and fallback model accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "AiTaskRoutes" ("TaskKey","IsEnabled")
        VALUES ('forbidden-no-model',true);
        RAISE EXCEPTION 'Enabled AI route without a primary model accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "AiModels" ("AiServerProviderId","NativeModelId")
        VALUES (provider_id,'model-A');
        RAISE EXCEPTION 'Duplicate native model within the same provider accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    INSERT INTO "AiProfileSettings" ("ProfileId","ProviderKey","DailyTokenBudget","MaxConcurrentJobs")
    VALUES (profile_id,'server',1000000,2);
    INSERT INTO "AiProfileOperationOverrides" ("ProfileId","OperationKey","ModelId")
    VALUES (profile_id,'novel-translation','model-B');
    INSERT INTO "AiUsageDaily"
       ("ProfileId","UtcDay","ProviderKey","ModelId","OperationKey",
        "Requests","InputTokens","OutputTokens")
    VALUES (profile_id,DATE '2026-10-10','server','model-A','novel-translation',
            3,4500,1200);

    BEGIN
        UPDATE "AiProfileSettings" SET "ProviderKey"='unknown' WHERE "ProfileId"=profile_id;
        RAISE EXCEPTION 'Unknown AI provider type accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE "AiProfileSettings" SET "AiTranslationModeTypeId"=255 WHERE "ProfileId"=profile_id;
        RAISE EXCEPTION 'Unknown AI translation mode allowed';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        UPDATE "AiProfileSettings" SET "DailyTokenBudget"=0 WHERE "ProfileId"=profile_id;
        RAISE EXCEPTION 'Zero daily AI token budget accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "AiProfileOperationOverrides" ("ProfileId","OperationKey")
        VALUES (profile_id,'book-qa');
        RAISE EXCEPTION 'Empty AI operation override accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE "AiUsageDaily" SET "InputTokens"=-1
        WHERE "ProfileId"=profile_id AND "UtcDay"=DATE '2026-10-10';
        RAISE EXCEPTION 'Negative AI usage metrics accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "AiUsageDaily"
           ("ProfileId","UtcDay","ProviderKey","ModelId","OperationKey")
        VALUES (profile_id,DATE '2026-10-10','server','model-A','novel-translation');
        RAISE EXCEPTION 'Duplicate per-day AI usage key accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    IF (SELECT count(*) FROM "AiUsageDaily" WHERE "ProfileId"=profile_id) <> 1 THEN
       RAISE EXCEPTION 'AI usage must have exactly one row per profile/day/task/model';
    END IF;
    RAISE NOTICE 'AI provider/model/routes, profile settings, operation override and UTC counters passed';
END $phase_b_ai$;
ROLLBACK;
