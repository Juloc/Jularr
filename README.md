# Jularr

![Docker](https://img.shields.io/badge/Docker-first-2496ED?logo=docker&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Platforms](https://img.shields.io/badge/Platforms-Web%20%7C%20PWA%20%7C%20Android%20%7C%20Android%20TV-2E7D32)

![Jularr](src/Jularr.Web/wwwroot/brand/hero-fuji.webp)

Jularr is a self-hosted Japanese learning companion and personal media library. It combines local media, reading, playback, vocabulary, progress and acquisition in one application while keeping the library on storage you control.

Jularr is currently prerelease software. The canonical application version lives in `src/Jularr.Web/Jularr.Web.csproj`; published builds are listed under [GitHub Releases](https://github.com/Juloc/Jularr/releases).

## Current scope

- Anime playback with Japanese subtitle learning, vocabulary lookup and per-profile progress.
- Manga, light-novel and book reading with persisted reader state and profile preferences.
- Local/NAS library scanning, metadata, artwork, media analysis and storage-aware playback behavior.
- FSRS-based learning/review state and local Japanese language tooling.
- Usenet acquisition through the canonical indexer/download-client pipeline.
- Optional AniList and AI integrations; neither is required for the core local library.
- Web/PWA plus Android and Android TV clients using the same server-side state and contracts.

Detailed operational behavior belongs in the focused documentation below rather than in this README.

## Deployment

The current runtime uses **PostgreSQL** as its canonical database.

Use the repository-root [compose.yaml](compose.yaml) with an immutable release tag and PostgreSQL.

Start here:

- [Deployment](docs/DEPLOYMENT.md)
- [Persistence and one-time migration](docs/PERSISTENCE.md)
- [Admin operations](docs/ADMIN_OPERATIONS.md)

## Documentation

| Area | Canonical document |
| --- | --- |
| Deployment | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) |
| Persistence / PostgreSQL | [docs/PERSISTENCE.md](docs/PERSISTENCE.md) |
| Administration and operations | [docs/ADMIN_OPERATIONS.md](docs/ADMIN_OPERATIONS.md) |
| Playback and media inventory | [docs/PLAYBACK.md](docs/PLAYBACK.md) |
| Android and Android TV clients | [docs/ANDROID_CLIENTS.md](docs/ANDROID_CLIENTS.md) |
| Anime acquisition | [docs/ANIME_ACQUISITION.md](docs/ANIME_ACQUISITION.md) |
| Release selection | [docs/AUTOMATIC_RELEASE_SELECTION.md](docs/AUTOMATIC_RELEASE_SELECTION.md) |
| Anime naming | [docs/ANIME_NAMING.md](docs/ANIME_NAMING.md) |
| Offline library | [docs/OFFLINE_LIBRARY.md](docs/OFFLINE_LIBRARY.md) |
| Media segments | [docs/MEDIA_SEGMENTS.md](docs/MEDIA_SEGMENTS.md) |
| Architecture | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| UI/UX specifications and mockups | [docs/mockups/README.md](docs/mockups/README.md) |
| Engineering conventions | [docs/MAINTAINABILITY_CONVENTIONS.md](docs/MAINTAINABILITY_CONVENTIONS.md) |

## Development

Before repository work, read [AGENTS.md](AGENTS.md), `.agent/project.yaml` and the linked engineering conventions.

Repository validation:

```bash
npm ci
dotnet restore Jularr.sln
dotnet build Jularr.sln --no-restore
dotnet test Jularr.sln --no-build
```

The test and persistence paths use PostgreSQL-compatible behavior; see [docs/PERSISTENCE.md](docs/PERSISTENCE.md).

## Documentation rule

The README intentionally stays short. Detailed behavior belongs in the canonical document for that area and is linked from here.

Current documentation uses the **Jularr** product name. Documentation and runtime identifiers use the Jularr name.
