#!/usr/bin/env python3
"""Low-impact real-HTTP audit of CAPTCHA/front-door enforcement on an owned Jularr instance.

Attempts access without completing CAPTCHA, forging clearance, or contacting other origins.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qsl, urlencode, urljoin, urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

MAX_TARGETS = 5
MAX_RESPONSE = 65536
MAX_TIMEOUT = 20.0
CHALLENGE_MARKERS = (
    b"cf-challenge", b"cf-mitigated", b"challenge-platform", b"hcaptcha",
    b"h-captcha", b"g-recaptcha", b"recaptcha", b"captcha",
    b"checking your browser", b"verify you are human", b"just a moment",
)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None


@dataclass
class Probe:
    path: str
    variant: str
    method: str
    status: int | None
    result: str
    reason: str
    elapsed_ms: int
    content_type: str = ""
    bytes_read: int = 0


def allowed_base(raw: str) -> str:
    parsed = urlsplit(raw)
    if not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/"):
        raise ValueError("Base URL must contain only a host (and optional port); no userinfo, path, query, or fragment.")
    if parsed.scheme != "https" and not (parsed.scheme == "http" and parsed.hostname in ("localhost", "127.0.0.1", "::1")):
        raise ValueError("Live tests require HTTPS. HTTP is permitted only on a loopback development server.")
    try:
        port = parsed.port
    except ValueError as error:
        raise ValueError("Invalid base URL port.") from error
    if port is not None and not 1 <= port <= 65535:
        raise ValueError("Invalid base URL port.")
    return urlunsplit((parsed.scheme, parsed.netloc, "", "", ""))


def target_url(base: str, target: str) -> str:
    if not target.startswith("/") or target.startswith("//") or "\\" in target or "\n" in target or "\r" in target:
        raise ValueError("Targets must be absolute same-origin paths starting with a single '/'.")
    parsed = urlsplit(target)
    if parsed.scheme or parsed.netloc or parsed.fragment:
        raise ValueError("Target must be a local path with an optional query, not a URL or fragment.")
    if any(segment in ("..", ".") for segment in parsed.path.split("/")):
        raise ValueError("Path traversal is not allowed.")
    url = urljoin(base + "/", target)
    if (urlsplit(url).scheme, urlsplit(url).netloc) != (urlsplit(base).scheme, urlsplit(base).netloc):
        raise ValueError("Test target must remain on the configured origin.")
    return url


def variants(url: str):
    parsed = urlsplit(url)
    query = parse_qsl(parsed.query, keep_blank_values=True)
    query.append(("__jularr_audit", "1"))
    busted = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, urlencode(query), ""))
    candidate = parsed.path.rstrip("/") + "/" if parsed.path != "/" and not parsed.path.endswith("/") else None
    trailing = urlunsplit((parsed.scheme, parsed.netloc, candidate, parsed.query, "")) if candidate else None
    cases = [
        ("anonymous", "GET", url, {}),
        ("json_accept", "GET", url, {"Accept": "application/json"}),
        ("xhr_request", "GET", url, {"X-Requested-With": "XMLHttpRequest", "Accept": "application/json"}),
        ("proxy_header_trust", "GET", url, {"X-Forwarded-For": "127.0.0.1"}),
        ("range_download", "GET", url, {"Range": "bytes=0-1023"}),
        ("head_request", "HEAD", url, {}),
        ("cache_buster", "GET", busted, {"Cache-Control": "no-cache"}),
    ]
    if trailing:
        cases.append(("trailing_slash", "GET", trailing, {}))
    return cases


def classify(status: int, headers, data: bytes, method: str, proof: bytes | None) -> tuple[str, str]:
    content_type = headers.get("Content-Type", "").lower()
    if (headers.get("cf-mitigated", "").lower() == "challenge" or (
        "html" in content_type and any(marker in data.lower() for marker in CHALLENGE_MARKERS)
    )):
        return "BLOCKED", "Challenge detected"
    if proof and proof in data and 200 <= status < 300:
        return "EXPOSED", "Known protected bytes were returned without CAPTCHA clearance"
    if status in (401, 403, 407, 429, 503):
        return "BLOCKED", "Request rejected or rate-limited"
    if 300 <= status <= 399:
        return "REDIRECT", "Redirect not followed (may be login or challenge)"
    if status == 404:
        return "NOT_FOUND", "No resource at this path"
    if method == "HEAD" and 200 <= status < 300:
        return "REVIEW", "HEAD succeeded; body access not proven"
    if status == 206 and headers.get("Content-Range"):
        return "SUSPECT", "Byte-range of resource returned without CAPTCHA clearance"
    if 200 <= status < 300 and "attachment" in headers.get("Content-Disposition", "").lower():
        return "SUSPECT", "Download attachment returned without CAPTCHA clearance"
    if 200 <= status < 300:
        return "REVIEW", "Success response; specify --proof to distinguish private content from a login page"
    return "REVIEW", "Unexpected status; inspect front-door policy"


def probe(opener, path: str, variant: tuple, cookie: str | None, timeout: float, proof: bytes | None) -> Probe:
    name, method, url, additional_headers = variant
    safe_path = urlsplit(path).path  # Do not record potentially sensitive query strings.
    headers = {"User-Agent": "Jularr-Owned-Gate-Audit/1.0", "Accept": "text/html, */*;q=0.8", **additional_headers}
    if cookie:
        headers["Cookie"] = cookie
    request = Request(url, headers=headers, method=method)
    started = time.monotonic()
    try:
        try:
            response = opener.open(request, timeout=timeout)
        except HTTPError as error:
            response = error
        with response:
            status = response.status
            data = response.read(MAX_RESPONSE) if method != "HEAD" else b""
            result, reason = classify(status, response.headers, data, method, proof)
            return Probe(safe_path, name, method, status, result, reason, round((time.monotonic() - started) * 1000),
                         response.headers.get("Content-Type", "").split(";")[0][:80], len(data))
    except (URLError, OSError, TimeoutError, ValueError) as error:
        return Probe(safe_path, name, method, None, "ERROR", type(error).__name__, round((time.monotonic() - started) * 1000))


def run_audit(base: str, paths: list[str], proof: bytes | None, cookie: str | None,
              delay: float = 1.0, timeout: float = 10.0) -> dict:
    opener = build_opener(NoRedirect())  # No redirects, cookie jar, clearance acquisition, or JavaScript execution.
    results = []
    for path in paths:
        url = target_url(base, path)
        for variant in variants(url):
            if results:
                time.sleep(delay)
            test = probe(opener, path, variant, cookie, timeout, proof)
            results.append(asdict(test))
            if test.result == "EXPOSED":
                break  # Stop when exposure is confirmed to minimize live traffic.
    labels = {item["result"] for item in results}
    conclusion = ("EXPOSED" if "EXPOSED" in labels else "SUSPECT" if "SUSPECT" in labels else
                  "INCONCLUSIVE" if "REVIEW" in labels or "ERROR" in labels or "REDIRECT" in labels else "BLOCKED")
    return {
        "tool": "Jularr Gate Audit",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "origin": base,
        "conclusion": conclusion,
        "proof_supplied": proof is not None,
        "requests": len(results),
        "results": results,
        "note": "EXPOSED means the provided known-protected bytes were retrieved. BLOCKED is not proof of complete security."
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Audit CAPTCHA enforcement on an owned Jularr host with real HTTP requests.")
    parser.add_argument("--base-url", required=True, help="Owned Jularr origin, e.g. https://jularr.example.org")
    parser.add_argument("--path", action="append", required=True, help="Read-only protected path; repeat up to five times")
    parser.add_argument("--proof", help="Unique text that appears only in the protected response")
    parser.add_argument("--proof-hex", help="Unique protected binary signature in hexadecimal")
    parser.add_argument("--cookie-env", default="JULARR_AUDIT_COOKIE", help="Optional env var with login-only cookie; no CAPTCHA clearance")
    parser.add_argument("--delay", type=float, default=1.0, help="Seconds between requests, minimum 0.5")
    parser.add_argument("--timeout", type=float, default=10.0, help="Per-request timeout, maximum 20 seconds")
    parser.add_argument("--json", help="Optional JSON report output path")
    parser.add_argument("--confirm-owned", action="store_true", help="Confirm authorization to assess this instance")
    args = parser.parse_args(argv)
    try:
        if not args.confirm_owned:
            raise ValueError("Use --confirm-owned only for an instance you own or are authorized to test.")
        if len(args.path) > MAX_TARGETS:
            raise ValueError(f"At most {MAX_TARGETS} protected paths per run.")
        if args.proof and args.proof_hex:
            raise ValueError("Choose either --proof or --proof-hex.")
        if not 0.5 <= args.delay <= 30 or not 0.5 <= args.timeout <= MAX_TIMEOUT:
            raise ValueError("Delay must be 0.5–30 seconds and timeout 0.5–20 seconds.")
        base = allowed_base(args.base_url)
        for path in args.path:
            target_url(base, path)
        proof = bytes.fromhex(args.proof_hex) if args.proof_hex else args.proof.encode() if args.proof else None
        if proof is not None and not proof:
            raise ValueError("Protected proof cannot be empty.")
        cookie = os.getenv(args.cookie_env)
        if cookie and ("\n" in cookie or "\r" in cookie):
            raise ValueError("Session cookie contains invalid characters.")
        report = run_audit(base, args.path, proof, cookie, args.delay, args.timeout)
    except ValueError as error:
        parser.error(str(error))
    for item in report["results"]:
        print(f"{item['result']:<12} {item['method']:<5} {item['variant']:<18} {item['status'] or '-':<3} {item['path']} — {item['reason']}")
    print(f"Conclusion: {report['conclusion']} ({report['requests']} requests)")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as output:
            json.dump(report, output, indent=2)
            output.write("\n")
    return 2 if report["conclusion"] == "EXPOSED" else 1 if report["conclusion"] in ("SUSPECT", "INCONCLUSIVE") else 0


if __name__ == "__main__":
    sys.exit(main())
