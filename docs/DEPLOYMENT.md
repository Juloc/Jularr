# Deployment

Jularr is Docker-first and currently uses PostgreSQL as its single canonical database.

## Current deployment contract

A production deployment must provide:

- an immutable Jularr image tag such as `ghcr.io/juloc/jularr:<release-version>`; do not use `latest` as the canonical deployment path;
- PostgreSQL and a persistent database volume;
- `ConnectionStrings__Default` pointing Jularr at that PostgreSQL instance;
- persistent Jularr application data under `/data`;
- media/library mounts appropriate to the installation, normally read-only unless a feature explicitly requires managed writes;
- health/dependency handling that does not start normal application work against an unavailable database.

Published versions are listed under [GitHub Releases](https://github.com/Juloc/Jularr/releases).

The repository-root `compose.yaml` is being aligned with this contract under [issue #863](https://github.com/Juloc/Jularr/issues/863). Until that issue is completed and `docker compose config` plus a fresh deployment have been verified, do not treat older SQLite-only or mutable-`latest` examples as production guidance.

## Persistence and upgrades

PostgreSQL is the runtime source of truth. The legacy SQLite reader exists only for the supported one-time import path into an empty PostgreSQL target; it is not a normal runtime fallback.

See [PERSISTENCE.md](PERSISTENCE.md) for the persistence epoch, importer constraints and database architecture.

## Operations

See [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md) for administration, storage, scans, integrations, health and operational behavior.

For Android and Android TV clients, see [ANDROID_CLIENTS.md](ANDROID_CLIENTS.md).

## Reverse proxy

Internet-facing installations should terminate HTTPS at a trusted reverse proxy and keep Jularr and PostgreSQL on private networks. Do not expose the database publicly.

Exact proxy, Compose and migration commands must be validated against the current release before being promoted into the root README.
