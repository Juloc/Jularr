# Deployment

Jularr is Docker-first and uses PostgreSQL as its canonical database.

## Compose

The repository-root `compose.yaml` is the canonical generic deployment example.

1. Copy `.env.example` to `.env`.
2. Set `JULARR_VERSION` to an immutable published Jularr release tag.
3. Replace `JULARR_DB_PASSWORD` with a long random password.
4. Validate and start:

```bash
docker compose config
docker compose up -d
```

Open `http://localhost:8097` and create the owner account.

## Persistent data

The default Compose stack keeps three named volumes:

- `jularr-data` for Jularr application state under `/data`;
- `jularr-postgres` for PostgreSQL;
- `translategemma-models` only when the optional TranslateGemma profile is used.

Media libraries are separate mounts and should normally be mounted read-only unless a feature explicitly owns managed writes to that library.

## Configuration

The application requires `ConnectionStrings__Default`. The repository Compose file supplies it from the PostgreSQL service and `JULARR_DB_PASSWORD`.

Published application images should use immutable version tags. Do not use `latest` as the normal deployment pin.

## Reverse proxy

Internet-facing installations should terminate HTTPS at a trusted reverse proxy and keep Jularr and PostgreSQL on private networks. Do not expose PostgreSQL publicly.

## Operations

See [ADMIN_OPERATIONS.md](ADMIN_OPERATIONS.md) for server administration, storage, scans, integrations and health.

See [PERSISTENCE.md](PERSISTENCE.md) for the database architecture.

For Android and Android TV clients, see [ANDROID_CLIENTS.md](ANDROID_CLIENTS.md).
