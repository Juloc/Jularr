-- Nyaa is a keyless torrent index like the direct sources, but its type (10) was missing from the list that may go without an API key, so saving a Nyaa connection failed.
ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT "AcquisitionConnections_TypeKey_check";
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_TypeKey_check"
    CHECK ("AcquisitionConnectionTypeId" IN (1, 3, 4, 5, 6, 10, 11, 12) OR "ProtectedApiKey" IS NOT NULL);
