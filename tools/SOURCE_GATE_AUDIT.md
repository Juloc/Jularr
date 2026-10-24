# Live CAPTCHA Gate Audit (Jularr)

A small external security tool for **your own Jularr installation**. It sends real, low-volume HTTP requests to endpoints you specify and checks whether a visitor **without CAPTCHA clearance** can obtain protected content. This is not a CAPTCHA-solving service.

## Run

Requires only Python 3.10+ and the standard library. No changes to the running Jularr instance, proxy or database.

From the repository root:

```sh
python3 tools/source_gate_audit.py \
  --base-url https://your-jularr.example.org \
  --path /a-protected-read-only-page \
  --proof "A_UNIQUE_STRING_ONLY_IN_PRIVATE_CONTENT" \
  --confirm-owned \
  --json gate-audit-result.json
```

For a protected download use the real download path and `--proof-hex` with a *distinctive* byte sequence at the beginning of the expected file (not a generic file signature). The tool tries a limited `Range` request, so it need not fetch the entire file.

If authorization is separately required, set `JULARR_AUDIT_COOKIE` in your environment to a legitimate **login-only** cookie; do not include CAPTCHA clearance cookies. Cookies and protected proof bytes are never written to the report. Supply only read-only GET/HEAD paths with no side effects or secrets in the URL.

## What it actually tries

Each target receives up to eight real requests without solving a CAPTCHA: normal anonymous GET, JSON `Accept`, XHR request headers, forged local `X-Forwarded-For` (to detect proxy trust errors), partial file request with `Range`, HEAD, cache-busting query and an alternate trailing slash.

The auditor **does not** use fake source providers in its normal operation. It is a read-only access tester, not a reusable CAPTCHA circumvention engine. It does not forge CAPTCHA clearance tokens, solve challenges, run JavaScript or probe unknown website paths.

Constraints: explicit owned-host acknowledgement, single exact origin, HTTPS except localhost, at most five target paths, one-second delay by default, bounded read length (64 KiB), no redirect following and verified TLS.

## Real-browser test mode (modern browser-based bots)

The original CLI above checks HTTP-level protection. The additional **browser audit** executes
actual Chromium/Firefox/WebKit JavaScript, observes the rendered page, tests an in-page
same-origin file/API request, reloads with the cookies generated during the visit,
then repeats in a second **fresh browser profile**. It can also run a mobile viewport.
It never supplies a valid CAPTCHA clearance and never clicks or solves a challenge.
**Live mode uses your actual Jularr domain and routes; there is no fake source backend.**

Playwright is an **optional external audit-only dependency**. It is not included in
Jularr's ASP.NET/NPM production requirements, and no browser is hosted by Jularr.

```sh
python3 -m pip install playwright
python3 -m playwright install chromium

python3 tools/source_gate_browser_audit.py \
  --base-url https://your-jularr.example.org \
  --path /an-actual-protected-page \
  --proof "UNIQUE_TEXT_FROM_PROTECTED_RESPONSE" \
  --wait 5 \
  --confirm-owned \
  --json browser-audit.json
```

If you have a system Chromium already installed, use
`--browser-executable /usr/bin/chromium` rather than installing one.
Run the same checks with `--browser firefox` or `--browser webkit --mobile` after installing those Playwright browser builds.

The browser audit tests three meaningful stages without a human response:
1. **First visit**: does JavaScript immediately or after the configured wait reveal protected content?
2. **Request with browser cookies**: after initial page execution, can `fetch()` read up to 64 KiB of the protected API/file?
3. **Reload with same cookies and a second clean profile**: does a reused or new browser gain unauthorized access?

If a separate login is required before the CAPTCHA, set `JULARR_AUDIT_COOKIE` to a *legitimate
login-only session cookie*. Do not include clearance cookies or secrets in command-line arguments.
This cookie is attached independently to each test browser; cookie **values** and protected bytes
are never included in report JSON. Browser sessions are discarded after the run.

### Limits and interpretation

A verified `EXPOSED` means the provided **known protected proof** was read by an
automated browser without anyone completing the CAPTCHA. This is a real finding.
`BLOCKED` proves only that those particular scenarios were blocked.
Login pages, 404s and HTTP 200 responses without proof do **not** prove CAPTCHA resistance.

This is deliberately **not** a CAPTCHA-solving, fingerprint-spoofing, stealth,
rotating-proxy, or CAPTCHA-farm emulator; it cannot be equated with every sophisticated
commercial bot. Use your vendor's official test keys/staging mode and server-side
monitoring to evaluate challenge difficulty, false positives and IP-based throttling.

Focused tests (no external hosts contacted):

```sh
cd tools
python3 -m unittest -v test_source_gate_audit.py test_source_gate_browser_audit.py
```

Optional local Chromium JavaScript smoke test:
```sh
cd tools
JULARR_AUDIT_BROWSER=/usr/bin/chromium python3 -m unittest -v test_source_gate_browser_audit.py
```

## Interpretation

| Result | Meaning |
|---|---|
| EXPOSED | The supplied known protected content was retrieved without CAPTCHA clearance. Treat as a confirmed gate failure. |
| SUSPECT | A file response or byte range was accessible; verify whether it should be gated. |
| BLOCKED | The probed requests were refused/challenged. This does not prove that *every* route is secure. |
| INCONCLUSIVE | Unexpected success, redirect or error without proof of protected content. Add a unique `--proof` and inspect. |

HTML challenge pages that return HTTP 200 are not counted as success. Status 403 alone may also mean login/authorization refusal, not necessarily effective CAPTCHA enforcement. To isolate CAPTCHA, ensure the test resource is otherwise accessible to the test identity.

The test tool cannot tell whether a JavaScript/browser challenge is solvable automatically. Use separate authorized browser testing for that.

Exit codes: 0 blocked, 1 unclear/suspect, 2 confirmed exposed. JSON reports contain status, method, probe name and timing—not response bodies.

## Focused tests

```sh
cd tools
python3 -m unittest -v test_source_gate_audit.py
```

The unit tests use only a local loopback HTTP fixture to verify safety and classifications. **Actual audits use the Jularr URL you configure.** Do not test Anna's Archive or other third-party sites using this tool.
