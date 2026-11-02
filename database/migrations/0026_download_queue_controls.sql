-- The Downloads page steers each client: a download's priority (1 low, 2 normal, 3 high, 4 force) and its place in the client's queue,
-- and a speed limit for Jularr's own downloaders, one per download workspace (empty: none). SABnzbd and qBittorrent keep their limits themselves.
ALTER TABLE "Operations" ADD COLUMN "DownloadPriorityTypeId" smallint NOT NULL DEFAULT 2 CHECK ("DownloadPriorityTypeId" BETWEEN 1 AND 4);
ALTER TABLE "Operations" ADD COLUMN "QueuePosition" integer NULL CHECK ("QueuePosition" >= 0);
ALTER TABLE "DownloadStorageSettings" ADD COLUMN "MaxBytesPerSecond" bigint NULL CHECK ("MaxBytesPerSecond" > 0);
