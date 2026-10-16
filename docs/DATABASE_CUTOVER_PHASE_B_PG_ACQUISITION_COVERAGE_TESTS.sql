-- Phase B scratch-only: exact Wanted uniqueness and pack's SAME-Work coverage.
BEGIN;
DO $acquisition_coverage$
DECLARE
  work_a bigint;
  work_b bigint;
  edition_a bigint;
  wanted_a bigint;
  wanted_edition bigint;
  wanted_b bigint;
  op bigint;
  op_b bigint;
  client bigint;
  binding bigint;
  binding_b bigint;
BEGIN
  INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
  VALUES (1,'Phase B Coverage A') RETURNING "Id" INTO work_a;
  INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
  VALUES (1,'Phase B Coverage B') RETURNING "Id" INTO work_b;
  INSERT INTO "WorkEditions" ("WorkId","Name")
  VALUES (work_a,'Coverage edition') RETURNING "Id" INTO edition_a;
  INSERT INTO "WantedItems" ("WorkId") VALUES (work_a) RETURNING "Id" INTO wanted_a;
  INSERT INTO "WantedItems" ("WorkId","WorkEditionId")
  VALUES (work_a,edition_a) RETURNING "Id" INTO wanted_edition;
  INSERT INTO "WantedItems" ("WorkId") VALUES (work_b) RETURNING "Id" INTO wanted_b;

  BEGIN
    INSERT INTO "WantedItems" ("WorkId") VALUES (work_a);
    RAISE EXCEPTION 'Duplicate nullable-root Wanted was accepted';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;

  INSERT INTO "Operations" ("OperationKindKey","OperationStatusTypeId")
  VALUES ('ci-download',1) RETURNING "Id" INTO op;
  INSERT INTO "AcquisitionDownloadClients"
    ("DisplayName","BaseUrl","CredentialStorageKey")
  VALUES ('CI SAB','http://localhost:12345','ci-opaque-key')
  RETURNING "Id" INTO client;
  INSERT INTO "AcquisitionDownloadBindings"
    ("OperationId","AcquisitionDownloadClientId","AcquisitionKindTypeId","WorkId")
  VALUES (op,client,1,work_a) RETURNING "Id" INTO binding;

  INSERT INTO "AcquisitionDownloadWantedItems"
    ("AcquisitionDownloadBindingId","WantedItemId","WorkId")
  VALUES (binding,wanted_a,work_a),(binding,wanted_edition,work_a);

  BEGIN
    INSERT INTO "AcquisitionDownloadWantedItems"
      ("AcquisitionDownloadBindingId","WantedItemId","WorkId")
    VALUES (binding,wanted_b,work_a);
    RAISE EXCEPTION 'Foreign Work Wanted accepted by acquisition binding';
  EXCEPTION WHEN foreign_key_violation THEN NULL;
  END;
  -- Different valid Work-B binding prevents the duplicate PK from masking
  -- the intended mismatched WantedItem/work FK rejection.
  INSERT INTO "Operations" ("OperationKindKey","OperationStatusTypeId")
  VALUES ('ci-second-download',1) RETURNING "Id" INTO op_b;
  INSERT INTO "AcquisitionDownloadBindings"
    ("OperationId","AcquisitionDownloadClientId","AcquisitionKindTypeId","WorkId")
  VALUES (op_b,client,1,work_b) RETURNING "Id" INTO binding_b;
  BEGIN
    INSERT INTO "AcquisitionDownloadWantedItems"
      ("AcquisitionDownloadBindingId","WantedItemId","WorkId")
    VALUES (binding_b,wanted_a,work_b);
    RAISE EXCEPTION 'Foreign Work-A Wanted accepted by Work-B binding';
  EXCEPTION WHEN foreign_key_violation THEN NULL;
  END;
  RAISE NOTICE 'Phase B Wanted uniqueness and same-Work pack coverage passed';
END $acquisition_coverage$;
ROLLBACK;
