-- Phase-B draft 11 acceptance tests: run ONLY against clean disposable PostgreSQL.
-- Independent source domain Events + audience-scoped inbox/attempts; fixtures ROLLBACK.
BEGIN;
DO $phase_b_event_test$
DECLARE
    locale_id bigint;
    account_id bigint;
    profile_id bigint;
    other_profile_id bigint;
    event_id uuid;
    admin_event_id uuid;
    delivery_id bigint;
BEGIN
    -- Draft-only codes from the existing JularrEventAudience and Severity source.
    -- Their final enum : byte manifest remains B01, not inferred by this test.
    INSERT INTO "EventAudienceTypes" ("Id","Key","RequiresProfile")
    VALUES (1,'profile',true),(2,'admin',false);
    INSERT INTO "EventSeverityTypes" ("Id","Key")
    VALUES (1,'info'),(2,'warning'),(3,'critical');
    INSERT INTO "NotificationEventCategoryTypes" ("Id","Key")
    VALUES (1,'download-grabbed');
    INSERT INTO "NotificationChannelTypes" ("Id","Key")
    VALUES (1,'in-app');
    INSERT INTO "UiLocales" ("Locale","Name")
    VALUES ('phase-b-events','Phase B Events') RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-events@example.invalid','Events Fixture',1)
    RETURNING "Id" INTO account_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (account_id,'Event Profile',locale_id)
    RETURNING "Id" INTO profile_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (account_id,'Other Event Profile',locale_id)
    RETURNING "Id" INTO other_profile_id;
    INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
    VALUES (account_id,profile_id),(account_id,other_profile_id);

    INSERT INTO "Events"
        ("NotificationEventCategoryTypeId","EventAudienceTypeId",
         "EventSeverityTypeId","ProfileId","MessageKey","MessageParams")
    VALUES (1,1,1,profile_id,'notifications.event.downloadGrabbed','{"work":"Fixture"}'::jsonb)
    RETURNING "Id" INTO event_id;

    -- An Event without its required profile fails; a profile on Admin event fails.
    BEGIN
        INSERT INTO "Events"
            ("NotificationEventCategoryTypeId","EventAudienceTypeId","EventSeverityTypeId","MessageKey")
        VALUES (1,1,1,'bad-no-profile');
        RAISE EXCEPTION 'Event accepted without mandatory Profile';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "Events"
            ("NotificationEventCategoryTypeId","EventAudienceTypeId","EventSeverityTypeId","ProfileId","MessageKey")
        VALUES (1,2,1,profile_id,'bad-admin-profile');
        RAISE EXCEPTION 'Admin event accepted a Profile target';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- A canonical Profile event is visible only in its own Profile inbox.
    INSERT INTO "Notifications" ("EventId","ProfileId")
    VALUES (event_id,profile_id);
    BEGIN
        INSERT INTO "Notifications" ("EventId","ProfileId")
        VALUES (event_id,other_profile_id);
        RAISE EXCEPTION 'Wrong Profile received Event';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "Notifications" ("EventId","AccountId")
        VALUES (event_id,account_id);
        RAISE EXCEPTION 'Profile event incorrectly delivered to Admin account inbox';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "Notifications" ("EventId","ProfileId")
        VALUES (event_id,profile_id);
        RAISE EXCEPTION 'Duplicate Profile inbox item was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    INSERT INTO "NotificationDeliveries"
        ("EventId","ProfileId","NotificationChannelTypeId")
    VALUES (event_id,profile_id,1)
    RETURNING "Id" INTO delivery_id;
    BEGIN
        INSERT INTO "NotificationDeliveries"
            ("EventId","AccountId","NotificationChannelTypeId")
        VALUES (event_id,account_id,1);
        RAISE EXCEPTION 'Profile event incorrectly delivered through Admin channel';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "NotificationDeliveries"
            ("EventId","ProfileId","NotificationChannelTypeId")
        VALUES (event_id,other_profile_id,1);
        RAISE EXCEPTION 'Delivery accepted unrelated Profile';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "NotificationDeliveries"
            ("EventId","ProfileId","NotificationChannelTypeId")
        VALUES (event_id,profile_id,1);
        RAISE EXCEPTION 'Duplicate delivery target was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    INSERT INTO "NotificationDeliveryAttempts"
        ("NotificationDeliveryId","AttemptNumber","Succeeded","DurationMs","ErrorCode")
    VALUES (delivery_id,1,false,25,'ci_retryable'),(delivery_id,2,true,10,NULL);
    BEGIN
        INSERT INTO "NotificationDeliveryAttempts"
            ("NotificationDeliveryId","AttemptNumber","Succeeded","ErrorCode")
        VALUES (delivery_id,2,true,NULL);
        RAISE EXCEPTION 'Duplicate delivery attempt was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "NotificationDeliveryAttempts"
            ("NotificationDeliveryId","AttemptNumber","Succeeded","ErrorCode")
        VALUES (delivery_id,3,false,NULL);
        RAISE EXCEPTION 'Failed delivery without sanitized error code was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "NotificationDeliveryAttempts"
            ("NotificationDeliveryId","AttemptNumber","Succeeded","DurationMs")
        VALUES (delivery_id,3,true,-1);
        RAISE EXCEPTION 'Negative delivery duration was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO "Events"
        ("NotificationEventCategoryTypeId","EventAudienceTypeId","EventSeverityTypeId","MessageKey")
    VALUES (1,2,3,'notifications.event.storageProblem')
    RETURNING "Id" INTO admin_event_id;
    INSERT INTO "Notifications" ("EventId","AccountId")
    VALUES (admin_event_id,account_id);
    INSERT INTO "NotificationDeliveries" ("EventId","AccountId","NotificationChannelTypeId")
    VALUES (admin_event_id,account_id,1);
    BEGIN
        INSERT INTO "Notifications" ("EventId","ProfileId")
        VALUES (admin_event_id,profile_id);
        RAISE EXCEPTION 'Admin event incorrectly delivered to Profile inbox';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    RAISE NOTICE 'Phase B event audience, inbox isolation and delivery-attempt constraints passed';
END $phase_b_event_test$;
ROLLBACK;
