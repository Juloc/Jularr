-- Automatic and update backups share the keep limit, now 3 by default; manual backups are not counted. An installation still on the old default
-- of 7 follows; a number the Owner chose stays.
ALTER TABLE "InstanceSettings" ALTER COLUMN "BackupKeep" SET DEFAULT 3;
UPDATE "InstanceSettings"
SET
    "BackupKeep" = 3
WHERE
    "BackupKeep" = 7;
