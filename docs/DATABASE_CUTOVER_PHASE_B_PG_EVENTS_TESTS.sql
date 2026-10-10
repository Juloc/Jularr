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
    rejected_constraint text;
BEGIN
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

    INSERT INTO "NotificationSubscriptions" (
        "ProfileId", "NotificationEventCategoryTypeId", "IsEnabled", "NotificationTimingTypeId")
    VALUES (profile_id, 1, true, 2);
    INSERT INTO "NotificationSubscriptionChannels" (
        "ProfileId", "NotificationEventCategoryTypeId", "NotificationChannelTypeId")
    VALUES (profile_id, 1, 1), (profile_id, 1, 2), (profile_id, 1, 3);
    INSERT INTO "NotificationProfileChannels" ("ProfileId", "NotificationChannelTypeId", "IsEnabled")
    VALUES (profile_id, 2, false);
    IF (SELECT count(*) FROM "NotificationSubscriptionChannels" AS channel
        WHERE channel."ProfileId" = profile_id AND channel."NotificationEventCategoryTypeId" = 1) <> 3 THEN
        RAISE EXCEPTION 'Profile-wide gate erased selected event channels';
    END IF;
    BEGIN
        INSERT INTO "NotificationProfileChannels" ("ProfileId", "NotificationChannelTypeId")
        VALUES (other_profile_id, 2);
        RAISE EXCEPTION 'Push route implicitly enabled without explicit profile intent';
    EXCEPTION WHEN not_null_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "NotificationSubscriptionChannels" (
            "ProfileId", "NotificationEventCategoryTypeId", "NotificationChannelTypeId")
        VALUES (other_profile_id, 1, 1);
        RAISE EXCEPTION 'Selected channel without its parent event preference was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_NotificationSubscriptionChannels_NotificationSubscriptions' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "NotificationSubscriptions" SET "NotificationTimingTypeId" = 254 WHERE "ProfileId" = profile_id;
        RAISE EXCEPTION 'Unknown notification timing was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_NotificationSubscriptions_NotificationTimingTypes' THEN RAISE; END IF;
    END;

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
        VALUES (8,2,3,profile_id,'bad-admin-profile');
        RAISE EXCEPTION 'Admin event accepted a Profile target';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO "Events" (
            "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "MessageKey")
        VALUES (1, 2, 1, 'bad-category-audience');
        RAISE EXCEPTION 'DownloadGrabbed incorrectly widened its audience to Admin';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_Events_CategoryPolicy' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "Events" (
            "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey")
        VALUES (1, 1, 3, profile_id, 'bad-category-severity');
        RAISE EXCEPTION 'DownloadGrabbed incorrectly changed its severity to Critical';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_Events_CategoryPolicy' THEN RAISE; END IF;
    END;

    -- A canonical Profile event is visible only in its own Profile inbox.
    INSERT INTO "Notifications" ("EventId", "ProfileId", "NotificationGroupKey")
    VALUES (event_id, profile_id, 'event:' || event_id::text);
    BEGIN
        INSERT INTO "Notifications" ("EventId", "ProfileId", "NotificationGroupKey")
        VALUES (event_id, other_profile_id, 'event:' || event_id::text);
        RAISE EXCEPTION 'Wrong Profile received Event';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "Notifications" ("EventId", "AccountId", "NotificationGroupKey")
        VALUES (event_id, account_id, 'event:' || event_id::text);
        RAISE EXCEPTION 'Profile event incorrectly delivered to Admin account inbox';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "Notifications" ("EventId", "ProfileId", "NotificationGroupKey")
        VALUES (event_id, profile_id, 'event:' || event_id::text);
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
    VALUES (8,2,3,'notifications.event.storageProblem')
    RETURNING "Id" INTO admin_event_id;
    INSERT INTO "Notifications" ("EventId", "AccountId", "NotificationGroupKey")
    VALUES (admin_event_id, account_id, 'event:' || admin_event_id::text);
    INSERT INTO "NotificationDeliveries" ("EventId","AccountId","NotificationChannelTypeId")
    VALUES (admin_event_id,account_id,1);
    BEGIN
        INSERT INTO "Notifications" ("EventId", "ProfileId", "NotificationGroupKey")
        VALUES (admin_event_id, profile_id, 'event:' || admin_event_id::text);
        RAISE EXCEPTION 'Admin event incorrectly delivered to Profile inbox';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    INSERT INTO "NotificationEvents" ("NotificationId", "EventId", "ProfileId", "NotificationGroupKey")
    SELECT inbox."Id", inbox."EventId", inbox."ProfileId", inbox."NotificationGroupKey"
    FROM "Notifications" AS inbox
    WHERE inbox."ProfileId" = profile_id OR inbox."AccountId" = account_id;
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE NOTICE 'Phase B event audience, inbox isolation and delivery-attempt constraints passed';
END $phase_b_event_test$;
ROLLBACK;
