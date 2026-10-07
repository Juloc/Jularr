---
name: jularr-dev-env
description: Local Jularr development-environment operation and recovery - reuse or start the loopback web app (dotnet watch, port 5230), test PostgreSQL in Docker, locked executables, stale processes, Browser-pane access, no firewall prompts. Not an implementation skill.
---

# Jularr dev environment

Owns only local environment operation and recovery on this Windows machine (Git Bash + PowerShell, Docker data on the external D: drive, which must be connected). Implementation rules live in `jularr-task`. A user-level skill of the same name may carry machine-specific recovery steps and local scratch credentials; the rules below are the repository contract.

## Reuse before start

Before starting any app, database or process, check whether it already runs (`Get-NetTCPConnection -LocalPort 5230`, `Get-CimInstance Win32_Process` with command-line filter, `docker ps`). Reuse it when healthy; never start a duplicate `dotnet watch`, Web process, container or test database. Preserve a useful visual/demo database. Keep one logged-in local site available while doing UI work.

## Loopback and security

- Bind loopback only: `ASPNETCORE_URLS=http://127.0.0.1:5230`, started through `dotnet.exe` (`dotnet watch --non-interactive run --no-launch-profile`), with `DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1` and `DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1`. Never expose Jularr to LAN/public interfaces for convenience.
- Never create permanent firewall rules, disable the firewall, disable TLS verification, weaken OS security, or request elevation merely to run Jularr locally.
- If a startup method raises a firewall/security prompt: cancel it if possible, do not wait for the user, do not repeat the same method, switch to a loopback-only non-interactive one, and name the offending executable in the report.

## Locked executable / dotnet watch

If a build fails because the running Web executable is locked, identify the Jularr dev process (command line contains the worktree path and `watch`/`Jularr.Web`), stop or restart only that process, and rebuild once. Never blindly kill unrelated `dotnet` processes and never loop failed builds. Prefer reusing `dotnet watch` for live UI work; if it dies on a large edit, restart it once.

## Docker and PostgreSQL

Do not restart Docker or recreate a database because one test or build failed; first establish whether Docker/PostgreSQL is actually the cause (`docker info`, `docker ps`, container logs, Npgsql timeouts across unrelated tests). Test PostgreSQL uses the repository's existing test fixtures/container behavior; do not build a second ad-hoc DB setup. A visual/demo database is its own database on the existing test PostgreSQL container. Docker hang recovery (stop Docker processes, `wsl.exe --shutdown`, rename never delete stale socket folders, restart Docker Desktop de-elevated, never factory-reset, wait for PostgreSQL crash recovery) is a last resort after the cause is confirmed.

## Browser pane

Use the built-in browser pane against `http://127.0.0.1:5230`. If the session cookie is gone after a restart, sign in again with the local scratch account. Check the console during UI verification.

## Environment failure is not product failure

Do not change product code to work around unavailable Docker, a locked executable, a port conflict, a missing interactive login, a firewall prompt or a temporarily unavailable local service. Diagnose the environment separately and record the limitation if it prevents verification.

## Secrets

Never put passwords, tokens, cookies or connection secrets into source, commits, issues, skill files or logs meant for commit. Local scratch credentials stay in the session scratchpad, uncommitted and out of chat.
