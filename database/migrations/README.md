Numbered schema migrations after the frozen baseline: `NNNN_short_name.sql`, from `0002`, embedded automatically.
Rules: [Database migrations](../../docs/DATABASE_MIGRATIONS.md).

[0008_nyaa_keyless_connections.sql](0008_nyaa_keyless_connections.sql) permits the public Nyaa source without an API key. Existing connections and other types' key requirements stay intact. The normal upgrade backup and transaction apply; rollback restores that backup with the previous release.
