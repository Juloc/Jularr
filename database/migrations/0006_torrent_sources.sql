INSERT INTO "AcquisitionConnectionTypes" ("Id", "Key", "AcquisitionConnectionRoleId") VALUES
    (9, 'Qbittorrent', 2),
    (10, 'Nyaa', 1)
ON CONFLICT ("Id") DO NOTHING;
