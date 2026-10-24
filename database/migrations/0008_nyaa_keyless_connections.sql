-- Nyaa is a public source, like the other keyless catalogs. Keep the existing key requirement for download and subtitle clients.
ALTER TABLE "AcquisitionConnections"
    DROP CONSTRAINT "AcquisitionConnections_check4",
    ADD CONSTRAINT "AcquisitionConnections_ApiKeyRequired"
        CHECK ("AcquisitionConnectionTypeId" IN (1, 3, 4, 5, 6, 10) OR "ProtectedApiKey" IS NOT NULL);
