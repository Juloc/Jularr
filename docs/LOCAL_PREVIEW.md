# Local clean-start preview

The dedicated checkout is `D:\jularr-clean-start-server\checkout`; do not use it
for development. It runs only `origin/clean-start`, independently of developer branches.

- URL: https://localhost:7241/Account/Login. HTTP localhost:5241 redirects to HTTPS.
  Loopback by default. Optional LAN access binds only the configured IPv4 address,
  not all interfaces. HTTP remains loopback-only.
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

## Design review

Use `/Design` on this same Development host. The workshop renders the canonical
components, labeled samples and disabled Player chrome; it does not start playback.
Appearance controls adjust Original/Clean, brightness, accent and glass transparency
immediately in this browser. Shared component changes apply to real pages as well.
Review one component group at a time, then desktop/mobile and light/dark. Server
updates continue to follow green, integrated `clean-start` commits; there is no separate
HTML prototype to keep synchronized. Actual persistent mini-player navigation remains pending.

## Optional home-network access

The runner accepts `-LanAddress <private IPv4> -CertificateSubject <Windows certificate subject>`.
Keep the existing task arguments and append these only after provisioning a trusted
server certificate in that task user's `CurrentUser\My` store. It must cover `localhost`
and the LAN IP as a Subject Alternative Name, allow server authentication and have an
accessible private key. Certificate validation stays enabled; do not export private keys
or commit certificates/settings. The runner does not install certificates or open the firewall.

The Windows firewall needs one inbound TCP rule for the HTTPS port, restricted to the
selected local address and the actual home subnet. Creating it requires Administrator
rights. Do not open the database, HTTP port, all remote addresses or router port forwarding.
If Windows classifies this trusted home Wi-Fi as Public, keep the same address/subnet
restriction rather than broadly changing the network's firewall policy.

Other devices need to trust the public certificate authority explicitly; installing a CA
grants certificate trust and requires owner approval. Only distribute its public certificate,
never a PFX/private key. Devices that cannot install it may not support playback securely.
Use `https://<IPv4>:7241/Design` after setup; `localhost` always means the current device.
The LAN origin needs its own sign-in and browser appearance preferences. Reserve the
server's LAN IP in the router; an address change requires a matching certificate and rule.
`status.json` reports `LanUrl` when enabled. Stop/restart only the named preview task to
apply runner arguments; ordinary restarts retain the persisted sessions.
