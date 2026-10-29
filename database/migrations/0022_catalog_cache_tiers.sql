-- How much of the provider catalogs Jularr keeps is decided for the whole instance by the Caching module (key DiscoverCache) and its sub-option
-- LocalDiscovery (keep the full catalog); both keep their state. The choices each catalog had of its own go.
ALTER TABLE "ExternalWorkSyncs"
    DROP COLUMN "IsEnabled",
    DROP COLUMN "DetailsTrending",
    DROP COLUMN "CacheTier";

-- A sub-option of the Caching module: titles in a Discover list get their details in advance.
ALTER TABLE "InstanceSettings"
    ADD COLUMN IF NOT EXISTS "DetailsDiscoverAhead" boolean NOT NULL DEFAULT FALSE;
