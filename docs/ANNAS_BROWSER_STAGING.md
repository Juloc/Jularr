# Anna’s Archive: disposable Debian browser-egress verification

**Status: unverified release gate.** This procedure supports [#1483](https://github.com/Juloc/Jularr/issues/1483); it is not a passing report or a network isolation implementation. The existing local HTTPS CONNECT proxy is defense in depth, not proof that Chrome cannot open a direct socket.

## Environment (staging only)

Use a disposable Debian VM and isolated Docker network with fresh PostgreSQL, DataPath and media volumes. Never attach a NAS, production database, Docker socket or credentials to the Jularr container being tested. Record the exact Git SHA, Debian release, Docker release, Jularr image digest, browser version, network mode and relevant container privileges.

Enable Acquisition and the optional Browser module, then verify the managed Chrome is running in /Admin/Browser. Do not change Chrome startup flags, fingerprint, production CDP behavior, user agent or browser profile to influence the test result. Browser remains **OFF by default**; its administrative one-click setup is separately handled by PR #1606.

## Probe real Chrome against local sentinels

From a checkout of this commit on the disposable Debian host, select the actual staging container name. This command starts a temporary Node helper in **that container’s network namespace**, with read-only access to the managed browser’s profile and a read-only mount of the probe file; it does not install Node in Jularr:

~~~sh
TARGET=jularr-staging
# Set TARGET to the name shown by: docker ps --format '{{.Names}}'
docker run --rm \
  --network "container:$TARGET" \
  --volumes-from "$TARGET:ro" \
  --mount "type=bind,source=$PWD/scripts/annas-browser-egress-probe.mjs,target=/probe.mjs,readonly" \
  node:24-bookworm-slim node /probe.mjs
~~~

The script discovers the managed Chrome DevTools port at /data/browser/managed/profile/browser.port (or accepts --port/--port-file explicitly), opens a disposable test tab, and serves self-hosted sentinel endpoints at 127.0.0.1, ::1 if supported and the container’s own IPv4 address. From the tab it attempts HTTP, HTTPS, iframe, image, script, fetch and WebSocket connections to **those sentinels only**. No NAS, router, Docker management endpoint or cloud metadata target is touched. The tab is closed afterward.

Exit code **1**: at least one sentinel socket/HTTP hit, demonstrable bypass — FAIL. Exit code **2**: script or environment error — INCONCLUSIVE. Exit code **0**: no sentinel hits — **LIMITED PASS only**. The test page uses a data: URL; Chrome’s own Private Network Access protections can suppress requests before socket creation. Consequently, a limited pass is **not** evidence of a kernel-enforced deny policy and does not exercise public-to-private redirect chains or DNS rebinding.

Optionally use --seconds 15, but do not increase test duration without a reason.

## Mandatory independent evidence for #1483

1. On the same isolated staging network, serve an owner-controlled public HTTPS fixture for redirect chains and subresources; perform packet-level capture of **browser-originated traffic** for public/private mixed DNS responses, DNS rebinding, private/link-local IPv4 and IPv6, redirects, iframe, image, script, fetch, WebSocket and UDP. Record failures and blocked attempts without disclosing secrets. Sentinel callbacks alone cannot establish a missing socket.
2. Enforce and demonstrate a **browser-scoped OS/network egress boundary** that covers browser processes, subprocesses and UDP, including after a crash/restart. Proxy flags are not sufficient. It must not break the normal Jularr access to PostgreSQL, NAS, Usenet or other providers. Do not add unreviewed global firewall rules to a production container.
3. Run the actual authorized Anna flow with one legally accessible/public-domain book and one active download: Request → Wanted → Search → Grab → Download/manual confirmation → Import → Library → Reader. Test configured .org failure, mirror .gl/.pk/.gd selection, no key and a redacted test key. Verify the member key never reaches a different mirror/CDN or a client log, and validate EPUB/PDF bytes, edition, format, language and real Reader opening.
4. Record Provider/Browser/Acquisition OFF, authorization/profile ownership, missing Chrome, human challenge, invalid HTML, 429/503, expired URL, storage limit, interrupted restart/recovery and cancellation. A challenge HTML page saved as an .epub is a FAIL, not a download.
5. Record PASS / FAIL / BLOCKED / NOT TESTED for each scenario, exact command, relevant redacted trace/log path and date; attach the signed-off result to #1483. Until process isolation and actual Debian E2E are proven, do not release or production-enable the Browser module.

The broader [#1450](https://github.com/Juloc/Jularr/issues/1450) review is independent. This checklist does not claim it is complete.
