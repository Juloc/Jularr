# Releasing Jularr

A release is a git tag `v<MAJOR>.<MINOR>.<PATCH>` (for example `v0.2.0`) on a commit of `clean-start`. The [Release workflow](../.github/workflows/release.yml) runs only for such a tag; merging a PR publishes nothing.

## Before tagging

1. The release commit is on `origin/clean-start` and its **Frontend** and **Backend Full** runs are green (`gh run list --commit <sha>`). `Backend Full` runs on every push to `clean-start`; `Frontend` only when frontend or backend files changed, so after a docs-only commit start it by hand: `gh workflow run Frontend --ref clean-start`.
2. The remaining acceptance steps of [Database migrations](DATABASE_MIGRATIONS.md) are done.

## Release

```
git fetch origin && git tag v0.2.0 origin/clean-start && git push origin v0.2.0
```

A tag with a suffix (`v0.3.0-rc.1`) is a prerelease.

## What the workflow does

- **guard** fails the run unless the tag is `v<MAJOR>.<MINOR>.<PATCH>[-prerelease]`, the tagged commit is reachable from `origin/clean-start` or `origin/main`, and the newest completed Frontend and Backend Full runs of that exact commit succeeded (a missing or red run is named in the error).
- **image** builds the tagged commit for `linux/amd64`, passes the version into the build (the Admin System page shows it) and pushes `ghcr.io/juloc/jularr:0.2.0`, `:0.2` and `:latest`. A prerelease gets only its exact version tag, so `latest` and the minor tag never move to it. An already published version is never overwritten. The image carries the OCI labels `source`, `revision` and `version`.
- **release** creates the GitHub release with generated notes (a prerelease is marked as one). Re-run only this job if it failed after the image was pushed.

Rehearsal: *Actions, Release, Run workflow* with `dry_run` on (the default) runs the guard and builds the image without pushing or creating anything. It may be started from a branch; publishing needs the version tag.

## Updating a server

```
docker compose pull && docker compose up -d
```

Set `JULARR_VERSION` in `.env` to the new version first. Pending migrations run at startup after an automatic backup (database and keys, `/data/backups`); if the backup or a migration fails, the update is rolled back and Jularr shows a maintenance page. Rollback after a failed update: set `JULARR_VERSION` to the previous tag and start it again, the database is unchanged. After a successful update, going back means restoring the backup made before it ([Database migrations](DATABASE_MIGRATIONS.md)).
