-- What each provider catalog holds, counted by the worker after its passes instead of on every visit of the catalog and system pages:
-- its titles, those still to read and those only the catalog knows (no tie to the library). Empty until the worker's first count.
CREATE TABLE "CatalogNamespaceTotals" (
    "Namespace" varchar(16) PRIMARY KEY,
    "Entries" bigint NOT NULL CHECK ("Entries" >= 0),
    "Pending" bigint NOT NULL CHECK ("Pending" >= 0),
    "CatalogOnly" bigint NOT NULL CHECK ("CatalogOnly" >= 0),
    "CountedAt" timestamptz NOT NULL
);
