-- A news server may now be used with up to 100 connections; plans with many connections download faster.
ALTER TABLE "AcquisitionConnections" DROP CONSTRAINT IF EXISTS "AcquisitionConnections_MaxConnections_check";
ALTER TABLE "AcquisitionConnections" ADD CONSTRAINT "AcquisitionConnections_MaxConnections_check" CHECK ("MaxConnections" BETWEEN 1 AND 100);
