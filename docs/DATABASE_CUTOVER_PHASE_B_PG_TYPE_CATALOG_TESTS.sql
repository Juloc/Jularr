BEGIN;
DO $type_catalog$
DECLARE
    catalog_count integer;
    invalid_id smallint;
    invalid_key text;
    rejected_constraint text;
BEGIN
    SELECT count(*)
    INTO catalog_count
    FROM pg_class AS catalog
    INNER JOIN pg_namespace AS schema ON schema.oid = catalog.relnamespace
    INNER JOIN pg_attribute AS id ON id.attrelid = catalog.oid AND id.attname = 'Id'
    INNER JOIN pg_attribute AS key ON key.attrelid = catalog.oid AND key.attname = 'Key'
    WHERE schema.nspname = current_schema()
        AND catalog.relkind = 'r'
        AND catalog.relname LIKE '%Types'
        AND id.atttypid = 'smallint'::regtype
        AND id.attnotnull
        AND key.atttypid = 'text'::regtype
        AND key.attnotnull;

    IF catalog_count <> 46 THEN
        RAISE EXCEPTION 'Expected 46 current draft type catalogs, found %', catalog_count;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_class AS catalog
        INNER JOIN pg_namespace AS schema ON schema.oid = catalog.relnamespace
        CROSS JOIN (VALUES ('Id'), ('Key')) AS guard(column_name)
        WHERE schema.nspname = current_schema()
            AND catalog.relkind = 'r'
            AND catalog.relname LIKE '%Types'
            AND NOT EXISTS (
                SELECT 1
                FROM pg_constraint AS constraint_definition
                WHERE constraint_definition.conrelid = catalog.oid
                    AND constraint_definition.contype = 'c'
                    AND constraint_definition.convalidated
                    AND constraint_definition.conname = 'CK_' || catalog.relname || '_' || guard.column_name
            )
    ) THEN
        RAISE EXCEPTION 'A type catalog lacks a validated Id or Key CHECK';
    END IF;

    INSERT INTO "AccountRoleTypes" ("Id", "Key")
    VALUES (0, 'phase-b-byte-min'), (255, 'phase-b-byte-max');
    INSERT INTO "AcquisitionRuleFieldTypes" ("Id", "Key")
    VALUES (0, 'phase-b-byte-min'), (255, 'phase-b-byte-max');

    FOREACH invalid_id IN ARRAY ARRAY[-1, 256]::smallint[]
    LOOP
        BEGIN
            INSERT INTO "AccountRoleTypes" ("Id", "Key")
            VALUES (invalid_id, 'phase-b-invalid-byte');
            RAISE EXCEPTION 'AccountRoleTypes accepted out-of-byte Id %', invalid_id;
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_AccountRoleTypes_Id' THEN
                RAISE;
            END IF;
        END;

        BEGIN
            INSERT INTO "AcquisitionRuleFieldTypes" ("Id", "Key")
            VALUES (invalid_id, 'phase-b-invalid-byte');
            RAISE EXCEPTION 'AcquisitionRuleFieldTypes accepted out-of-byte Id %', invalid_id;
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_AcquisitionRuleFieldTypes_Id' THEN
                RAISE;
            END IF;
        END;
    END LOOP;

    FOREACH invalid_key IN ARRAY ARRAY['', '   ']
    LOOP
        BEGIN
            INSERT INTO "AccountRoleTypes" ("Id", "Key")
            VALUES (254, invalid_key);
            RAISE EXCEPTION 'AccountRoleTypes accepted a blank Key';
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_AccountRoleTypes_Key' THEN
                RAISE;
            END IF;
        END;

        BEGIN
            INSERT INTO "AcquisitionRuleFieldTypes" ("Id", "Key")
            VALUES (254, invalid_key);
            RAISE EXCEPTION 'AcquisitionRuleFieldTypes accepted a blank Key';
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_AcquisitionRuleFieldTypes_Key' THEN
                RAISE;
            END IF;
        END;
    END LOOP;

    BEGIN
        INSERT INTO "LearningMediaScopeTypes" ("Id", "Key")
        VALUES (0, 'phase-b-invalid-zero');
        RAISE EXCEPTION 'LearningMediaScopeTypes lost its nonzero scope boundary';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_LearningMediaScopeTypes_Id' THEN
            RAISE;
        END IF;
    END;
END $type_catalog$;
ROLLBACK;
