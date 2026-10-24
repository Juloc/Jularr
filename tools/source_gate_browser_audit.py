#!/usr/bin/env python3
"""Audit CAPTCHA enforcement with a real browser on an owned Jularr origin.

Executes page JavaScript and reuses its own session cookies, but never solves,
clicks, fakes or imports CAPTCHA clearance. No source/provider mocks in live runs.
The optional Playwright dependency is external to the Jularr application.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

from source_gate_audit import MAX_TARGETS, allowed_base, target_url

MAX_BODY = 65_536
MARKERS = ("checking your browser", "verify you are human", "just a moment",
           "cf-challenge", "challenge-platform", "captcha", "recaptcha", "hcaptcha")

# Check protected bytes within the browser itself; do not export response bodies.
# Bounded read avoids downloading a complete media file when Range is ignored.
READ_BROWSER_RESPONSE = r"""async ({url, proofHex, limit, timeoutMs}) => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(url, {
            method: 'GET', credentials: 'same-origin', redirect: 'manual',
            cache: 'no-store', headers: {'Range': 'bytes=0-65535'},
            signal: controller.signal
        });
        const chunks = [];
        const reader = response.body?.getReader();
        let size = 0;
        if (reader) {
            try {
                while (size < limit) {
                    const next = await reader.read();
                    if (next.done) break;
                    const chunk = next.value.slice(0, limit - size);
                    chunks.push(chunk);
                    size += chunk.length;
                }
            } finally {
                await reader.cancel().catch(() => {});
            }
        }
        const data = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) {
            data.set(chunk, offset);
            offset += chunk.length;
        }
        const proof = proofHex ? new Uint8Array(proofHex.match(/../g).map(x => parseInt(x, 16))) : null;
        let exposed = false;
        if (proof && proof.length && proof.length <= data.length) {
            outer: for (let i = 0; i <= data.length - proof.length; i++) {
                for (let j = 0; j < proof.length; j++) {
                    if (data[i + j] !== proof[j]) continue outer;
                }
                exposed = true;
                break;
            }
        }
        const type = response.headers.get('content-type') || '';
        const preview = type.includes('text/') || type.includes('json')
            ? new TextDecoder().decode(data.slice(0, 8192)).toLowerCase() : '';
        const markers = ['checking your browser', 'verify you are human', 'just a moment',
                         'cf-challenge', 'challenge-platform', 'captcha', 'recaptcha', 'hcaptcha'];
        return {
            status: response.status, exposed, bytesRead: size,
            challenge: response.headers.get('cf-mitigated') === 'challenge'
                || markers.some(x => preview.includes(x)),
            attachment: (response.headers.get('content-disposition') || '').toLowerCase().includes('attachment'),
            partial: response.status === 206 && !!response.headers.get('content-range')
        };
    } catch (error) {
        return {error: error?.name || 'BrowserFetchError'};
    } finally {
        clearTimeout(timeout);
    }
}"""


def classify(status: int | None, exposed: bool, challenge: bool,
             attachment: bool = False, partial: bool = False, error: str | None = None) -> str:
    if exposed:
        return "EXPOSED"
    if challenge or status in (401, 403, 407, 429, 503):
        return "BLOCKED"
    if error:
        return "INCONCLUSIVE"
    if attachment or partial:
        return "SUSPECT"
    return "INCONCLUSIVE"


def same_origin(base: str, candidate: str) -> bool:
    lhs, rhs = urlsplit(base), urlsplit(candidate)
    return (lhs.scheme, lhs.netloc) == (rhs.scheme, rhs.netloc)


def visible_content(page, proof: bytes | None) -> dict:
    try:
        text = page.locator("body").inner_text(timeout=2500)[:MAX_BODY]
        return {
            "exposed": bool(proof and proof in text.encode("utf-8")),
            "challenge": any(marker in text.lower() for marker in MARKERS)
        }
    except Exception:
        return {"exposed": False, "challenge": False}


def visit(page, url: str, base: str, proof: bytes | None, wait: float, timeout: float,
          session: str, stage: str) -> dict:
    started = time.monotonic()
    status, issue = None, None
    try:
        response = page.goto(url, wait_until="domcontentloaded", timeout=int(timeout * 1000))
        status = response.status if response else None
    except Exception as exc:
        issue = type(exc).__name__  # File downloads may interrupt document navigation.
    page.wait_for_timeout(int(wait * 1000))
    if not same_origin(base, page.url):
        return {"session": session, "stage": stage, "kind": "navigation", "status": status,
                "result": "INCONCLUSIVE", "reason": "No same-origin document loaded",
                "elapsed_ms": round((time.monotonic() - started) * 1000)}
    observed = visible_content(page, proof)
    verdict = classify(status, **observed, error=issue)
    return {"session": session, "stage": stage, "kind": "navigation", "status": status,
            "result": verdict,
            "reason": "Protected content visible" if observed["exposed"] else
                      "Challenge detected" if observed["challenge"] else issue or "No protected content proven",
            "elapsed_ms": round((time.monotonic() - started) * 1000)}


def browser_fetch(page, url: str, proof: bytes | None, timeout: float,
                  session: str, stage: str) -> dict:
    started = time.monotonic()
    response = page.evaluate(READ_BROWSER_RESPONSE, {
        "url": url, "proofHex": proof.hex() if proof else "",
        "limit": MAX_BODY, "timeoutMs": int(timeout * 1000)
    })
    verdict = classify(response.get("status"), response.get("exposed", False),
                       response.get("challenge", False), response.get("attachment", False),
                       response.get("partial", False), response.get("error"))
    return {"session": session, "stage": stage, "kind": "browser_fetch",
            "status": response.get("status"), "result": verdict,
            "reason": "Protected response bytes returned" if verdict == "EXPOSED" else
                      "Challenge detected" if verdict == "BLOCKED" else
                      response.get("error") or "No protected content proven",
            "bytes_read": response.get("bytesRead", 0),
            "elapsed_ms": round((time.monotonic() - started) * 1000)}


def run_browser_audit(base: str, paths: list[str], proof: bytes | None,
                      browser: str = "chromium", wait: float = 3.0, timeout: float = 15.0,
                      mobile: bool = False, executable: str | None = None,
                      login_cookie: str | None = None) -> dict:
    if browser not in ("chromium", "firefox", "webkit"):
        raise ValueError("Unsupported browser")
    if not 0 <= wait <= 15 or not 1 <= timeout <= 30:
        raise ValueError("Wait 0–15 seconds; timeout 1–30 seconds")
    if not paths or len(paths) > MAX_TARGETS:
        raise ValueError("Provide 1–5 read-only paths")
    urls = [target_url(base, path) for path in paths]
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as exc:
        raise RuntimeError("Install optional browser test tools: pip install playwright; playwright install chromium") from exc

    records = []
    with sync_playwright() as playwright:
        kind = getattr(playwright, browser)
        settings = {"headless": True}
        if executable:
            settings["executable_path"] = executable
        client = kind.launch(**settings)
        try:
            for url in urls:
                # Fresh anonymous browser profiles, no existing CAPTCHA clearance.
                for session in ("fresh", "second_fresh"):
                    options = {"viewport": {"width": 390 if mobile else 1440,
                                            "height": 844 if mobile else 900},
                               "accept_downloads": False}
                    if browser != "firefox":
                        options["is_mobile"] = mobile
                    context = client.new_context(**options)
                    try:
                        if login_cookie:
                            cookies = []
                            for segment in login_cookie.split(";"):
                                name, equal, value = segment.strip().partition("=")
                                if not equal or not name or any(char in name + value for char in "\r\n"):
                                    raise ValueError("Invalid login-only cookie")
                                cookies.append({"name": name, "value": value, "url": base})
                            context.add_cookies(cookies)
                        page = context.new_page()
                        page.on("popup", lambda popup: popup.close())

                        def restrict_navigation(route):
                            request = route.request
                            try:
                                main_navigation = request.is_navigation_request() and request.frame == page.main_frame
                            except Exception:
                                main_navigation = False
                            if main_navigation and not same_origin(base, request.url):
                                route.abort()
                            else:
                                route.continue_()

                        page.route("**/*", restrict_navigation)
                        records.append(visit(page, url, base, proof, wait, timeout, session, "initial"))
                        if records[-1]["result"] == "EXPOSED":
                            break
                        if page.url == "about:blank":
                            # Direct download navigation can leave about:blank; anchor JS
                            # to the Jularr origin before a bounded download fetch.
                            try:
                                page.goto(base + "/", wait_until="domcontentloaded",
                                          timeout=int(timeout * 1000))
                            except Exception:
                                pass
                        if same_origin(base, page.url):
                            records.append(browser_fetch(page, url, proof, timeout, session, "fetch_with_cookies"))
                            if records[-1]["result"] == "EXPOSED":
                                break
                            records.append(visit(page, url, base, proof, wait, timeout,
                                                 session, "reload_with_cookies"))
                            if records[-1]["result"] == "EXPOSED":
                                break
                    finally:
                        context.close()
                if records and records[-1]["result"] == "EXPOSED":
                    break
        finally:
            client.close()

    verdicts = {record["result"] for record in records}
    conclusion = ("EXPOSED" if "EXPOSED" in verdicts else
                  "SUSPECT" if "SUSPECT" in verdicts else
                  "INCONCLUSIVE" if "INCONCLUSIVE" in verdicts else "BLOCKED")
    return {"tool": "Jularr Real Browser Gate Audit",
            "timestamp_utc": datetime.now(timezone.utc).isoformat(),
            "origin": base, "browser": browser, "mobile": mobile,
            "proof_supplied": proof is not None,
            "conclusion": conclusion, "results": records,
            "note": "Known protected bytes required for exposure; no CAPTCHA was solved, clicked or faked."}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Audit your Jularr CAPTCHA gate with a real automated browser.")
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--path", action="append", required=True, help="Read-only protected path, up to five")
    parser.add_argument("--proof", help="Unique text visible in the protected response")
    parser.add_argument("--proof-hex", help="Unique protected binary bytes, hexadecimal")
    parser.add_argument("--browser", choices=("chromium", "firefox", "webkit"), default="chromium")
    parser.add_argument("--mobile", action="store_true", help="Small touch viewport")
    parser.add_argument("--wait", type=float, default=3.0, help="Wait 0–15 seconds for JavaScript")
    parser.add_argument("--timeout", type=float, default=15.0, help="Timeout 1–30 seconds")
    parser.add_argument("--browser-executable", help="Optional local browser binary")
    parser.add_argument("--cookie-env", default="JULARR_AUDIT_COOKIE", help="Login-only cookie env var; never CAPTCHA clearance")
    parser.add_argument("--confirm-owned", action="store_true")
    parser.add_argument("--json", dest="json_file", help="Save sanitized report")
    args = parser.parse_args(argv)
    try:
        if not args.confirm_owned:
            raise ValueError("Use --confirm-owned only for a Jularr instance you may audit")
        base = allowed_base(args.base_url)
        if args.proof and args.proof_hex:
            raise ValueError("Specify only one proof type")
        proof = bytes.fromhex(args.proof_hex) if args.proof_hex else args.proof.encode() if args.proof else None
        if proof is not None and not proof:
            raise ValueError("Empty protected proof")
        report = run_browser_audit(base, args.path, proof, args.browser, args.wait, args.timeout,
                                   args.mobile, args.browser_executable, os.getenv(args.cookie_env))
    except (RuntimeError, ValueError) as exc:
        parser.error(str(exc))
    for row in report["results"]:
        print(f"{row['result']:<12} {row['session']:<13} {row['stage']:<21} "
              f"{row['status'] or '-':<3} {row['reason']}")
    print("Conclusion:", report["conclusion"])
    if args.json_file:
        with Path(args.json_file).open("w", encoding="utf-8") as output:
            json.dump(report, output, indent=2)
            output.write("\n")
    return 2 if report["conclusion"] == "EXPOSED" else 0 if report["conclusion"] == "BLOCKED" else 1


if __name__ == "__main__":
    sys.exit(main())
