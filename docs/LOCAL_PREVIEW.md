# Local clean-start preview

The dedicated checkout is `D:\jularr-clean-start-server\checkout`; do not use it
for development. It runs only `origin/clean-start`, independently of developer branches.

- URL: https://localhost:7241/Account/Login. HTTP localhost:5241 redirects to HTTPS.
  Loopback only; not exposed to the LAN.
- Windows task: `JularrCleanStartPreview`, current user, limited privileges, hidden;
  starts at that user's login, not before login or while the computer is off/asleep.
- Runner: [run-clean-start-preview.ps1](../scripts/run-clean-start-preview.ps1),
  deployed as `D:\jularr-clean-start-server\run-preview.ps1` and run by PowerShell 7.
  Explicit Git/.NET/npm/Docker paths avoid reliance on the scheduled-task PATH.
  A valid user HTTPS dev certificate is required.
- Git trusts only this dedicated checkout per command (`safe.directory`), because
  this D: filesystem does not record file ownership; no global wildcard exception.
- Every 60 seconds: fetch, fast-forward only, install locked Node dependencies,
  build fingerprinted assets, publish outside the checkout, then replace the host
  after a successful build. Startup health check uses the login page; a failed
  startup restores the previous release. Builds retain the running host.
- Sessions use the same persisted Windows Data-Protection keys and the stable `Jularr`
  application context across release folders. Ordinary restarts/deployments preserve
  authentication; the default persistent login lasts 30 days and renews during use.
  Adopting the stable context requires signing in once again.
- Dirty/divergent checkouts are never reset. Fetch/build errors retain the current
  process and retry; deployments can therefore lag behind clean-start until resolved.
- Status: `status.json` (running commit, URL, host PID); logs: `logs\controller.log`,
  `build-<commit>.log`, `host-<commit>.out.log` and `.err.log`. Releases are retained;
  disk cleanup is manual, never an automatic broad delete.

## Preview database and test library

`D:\jularr-clean-start-server\preview.settings.json` is local and never committed. It holds
`Environment` variables for the host (`JULARR_DB`, `Storage__LocalPaths__0`, FFmpeg paths,
transcode cache), the `Database` container/name/user and the `Seed` owner, local path and
library roots. The database runs in the dedicated loopback container `jularr-preview-pg`;
it is never an active installation.

The runner hashes the fresh baselines in its installation order. When the hash changes,
it drops and recreates only the preview database and installs the baselines
(`schema.installed`). After the host starts it creates, idempotently and through the
public services, the Owner, the local connection `D:\testlibrary` and its library roots,
and queues their scans; it retries every minute until this succeeds (`schema.sha256`). Preview data is disposable: a
baseline change resets it. A new baseline file must be added to the runner's order.
The Owner credentials are only in the settings file.
This reset also deletes account sessions and requires signing in again; session renewal
cannot preserve a deleted database. No baseline or preview-database change is part of
the session persistence fix.

To stop safely, create the exact marker `D:\jularr-clean-start-server\stop.request`;
the runner stops its own host and exits. To resume, remove only that marker and start
the named Windows task. A stale status file is not proof of a live process: check
the task and URL. Do not kill unrelated dotnet processes or remove other D: folders.
