# Local clean-start preview

The dedicated checkout is `D:\jularr-clean-start-server\checkout`; do not use it
for development. It runs only `origin/clean-start`, independently of developer branches.
No existing D: installation or database is modified.

- URL: https://localhost:7240/Account/Login (shell: https://localhost:7240/ShellPreview).
- HTTP localhost:5240 redirects to HTTPS. Loopback only; not exposed to the LAN.
- Windows task: `JularrCleanStartPreview`, current user, limited privileges, hidden;
  starts at that user's login, not before login or while the computer is off/asleep.
- Runner: [run-clean-start-preview.ps1](../scripts/run-clean-start-preview.ps1),
  deployed as `D:\jularr-clean-start-server\run-preview.ps1`. Explicit Git/.NET paths
  avoid reliance on the scheduled-task PATH. A valid user HTTPS dev certificate is required.
- Git trusts only this dedicated checkout per command (`safe.directory`), because
  this D: filesystem does not record file ownership; no global wildcard exception.
- Every 60 seconds: fetch, fast-forward only, publish outside the checkout, then
  replace the host after successful build. Startup health check uses the login page;
  a failed startup restores the previous release. Builds retain the running host.
- Dirty/divergent checkouts are never reset. Fetch/build errors retain the current
  process and retry; deployments can therefore lag behind clean-start until resolved.
- Status: `status.json` (running commit, URL, host PID); logs: `logs\controller.log`,
  `build-<commit>.log`, `host-<commit>.out.log` and `.err.log`. Releases are retained;
  disk cleanup is manual, never an automatic broad delete.

To stop safely, create the exact marker `D:\jularr-clean-start-server\stop.request`;
the runner stops its own host and exits. To resume, remove only that marker and start
the named Windows task. A stale status file is not proof of a live process: check
the task and URL. Do not kill unrelated dotnet processes or remove other D: folders.

## Current integration boundary

This is the unmodified clean-start Frontend preview, not an end-to-end authentication
server. The two old `Jularr.Data.*` imports in Frontend `Framework/BaseField.cs`
were corrected with explicit owner approval. The existing local HTTPS certificate
was trusted in the owner's CurrentUser store with approval; HTTPS was verified
without a certificate bypass. Other machines/users need their own certificate trust.
Its Program still lacks Backend service/API and PostgreSQL localization
registration. Login and API business behavior are not wired there yet; no preview
database is used or provisioned until that integration needs it. Any later database
must be an explicit isolated test database, never an active installation.
See [host registration](../README.md#backend-im-gemeinsamen-host).
