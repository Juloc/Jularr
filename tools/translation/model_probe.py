#!/usr/bin/env python3
"""Probe an EXISTING Ollama runtime; install only on explicit --pull.

No model weights, native inference runtime or Docker permissions are shipped
with Jularr. Ollama stores pulled models in its own persistent model volume.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import time
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
from urllib.error import HTTPError, URLError

# Fixed allowlist. Download sizes come from ollama.com/library/translategemma/tags.
# RAM values are conservative preliminary heuristics, NOT measured requirements.
MODELS = {
    "translategemma:4b": {"download_gb": 3.3, "estimated_ram_gb": 6},
    "translategemma:12b": {"download_gb": 8.1, "estimated_ram_gb": 14},
    "translategemma:27b": {"download_gb": 17, "estimated_ram_gb": 28},
}
LANG = {"id": "Indonesian", "de": "German", "ja": "Japanese", "en": "English"}
SAMPLES = [
    ("id", "de", "Rina pulang ke rumah. Dia membawa sebuah buku, tetapi hujan semakin deras."),
    ("ja", "de", "雨が強くなった。美咲は窓を閉めて、本を机の上に置いた。"),
    ("de", "id", "Die Tür war offen, doch niemand wagte es, das dunkle Zimmer zu betreten."),
]


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise ValueError("Ollama redirects are forbidden")


def endpoint(address: str) -> str:
    parsed = urlsplit(address)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("An operator-configured http(s) Ollama endpoint without credentials is required")
    if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
        raise ValueError("Ollama endpoint must contain only scheme, host and port")
    try:
        parsed.port
    except ValueError as exc:
        raise ValueError("Invalid Ollama port") from exc
    return address.rstrip("/")


def call(address: str, path: str, data=None, timeout=12, stream=False):
    body = None if data is None else json.dumps(data).encode("utf-8")
    request = Request(address + path, data=body, headers={"Content-Type": "application/json"})
    result = build_opener(NoRedirect()).open(request, timeout=timeout)
    if stream:
        return result
    with result:
        payload = result.read(2_097_153)
        if len(payload) > 2_097_152:
            raise ValueError("Oversized Ollama response")
        return json.loads(payload)


def memory(root=Path("/")) -> dict:
    """Host MemAvailable is not a container's RAM budget: cgroup memory.max wins."""
    result = {"available": None, "limit": None, "used": None, "origin": "unknown"}
    try:
        for line in (root / "proc/meminfo").read_text(encoding="ascii").splitlines():
            if line.startswith("MemAvailable:"):
                result.update(available=int(line.split()[1]) * 1024, origin="host-meminfo")
                break
    except (OSError, ValueError, IndexError):
        pass
    try:
        limit = (root / "sys/fs/cgroup/memory.max").read_text(encoding="ascii").strip()
        if limit != "max":
            cap = int(limit)
            used = int((root / "sys/fs/cgroup/memory.current").read_text(encoding="ascii"))
            if cap <= 0 or used < 0:
                raise ValueError("Invalid cgroup accounting")
            left = max(0, cap - used)
            result.update(limit=cap, used=used, origin="cgroup-v2-conservative")
            result["available"] = min(result["available"], left) if result["available"] is not None else left
    except (OSError, ValueError):
        pass
    return result


def cpu_quota(root=Path("/")):
    try:
        quota, period = (root / "sys/fs/cgroup/cpu.max").read_text(encoding="ascii").split()
        if quota != "max" and int(period) > 0:
            return round(int(quota) / int(period), 2)
    except (OSError, ValueError):
        pass
    return None


def fit(available, model, shared_budget=False) -> str:
    if not shared_budget or available is None:
        return "unknown: runtime hardware/limits may differ"
    requested = MODELS[model]["estimated_ram_gb"] * 1_000_000_000
    return "plausible, unverified" if available >= requested else "insufficient estimated headroom"


def hardware(shared_budget=False) -> dict:
    budget = memory()
    return {
        "jularr_memory": budget,
        "jularr_cpu_affinity_count": len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
        "jularr_cpu_cgroup_quota": cpu_quota(),
        "gpu_device_hints_only": {
            "nvidia": Path("/dev/nvidia0").exists(),
            "drm": Path("/dev/dri").exists(),
        },
        "warning": "Jularr's container RAM is NOT Ollama's available RAM unless the limits are explicitly shared; GPU device presence proves nothing about acceleration.",
        "candidates": {name: dict(details, fit=fit(budget["available"], name, shared_budget))
                       for name, details in MODELS.items()},
    }


def installed(address: str) -> dict:
    found = call(address, "/api/tags").get("models")
    if not isinstance(found, list):
        raise ValueError("Ollama model catalog unavailable")
    return {item["name"]: {"bytes": item.get("size"), "digest": item.get("digest")}
            for item in found if isinstance(item, dict) and isinstance(item.get("name"), str)}


def loaded(address: str) -> dict:
    found = call(address, "/api/ps").get("models")
    if not isinstance(found, list):
        raise ValueError("Ollama active model list unavailable")
    return {item["name"]: {"size_vram": item.get("size_vram"), "expires_at": item.get("expires_at")}
            for item in found if isinstance(item, dict) and isinstance(item.get("name"), str)}


def benchmark(address: str, model: str, samples=SAMPLES) -> list[dict]:
    if model not in MODELS:
        raise ValueError("Model not allowlisted")
    results = []
    for source, target, sample in samples:
        # Ollama's published TranslateGemma prompt includes TWO blank lines before
        # source text. No extra system role, glossary text or book-sized input.
        prompt = (
            f"You are a professional {LANG[source]} ({source}) to {LANG[target]} ({target}) translator. "
            f"Your goal is to accurately convey the meaning and nuances of the original {LANG[source]} text "
            f"while adhering to {LANG[target]} grammar, vocabulary, and cultural sensitivities.\n"
            f"Produce only the {LANG[target]} translation, without any additional explanations or commentary. "
            f"Please translate the following {LANG[source]} text into {LANG[target]}:\n\n\n{sample}"
        )
        start = time.monotonic()
        answer = call(address, "/api/generate", {
            "model": model, "prompt": prompt, "stream": False, "keep_alive": "0",
            "options": {"temperature": 0, "num_ctx": 2048, "num_predict": 192},
        }, timeout=600)
        response = answer.get("response")
        if not isinstance(response, str):
            raise ValueError("Model returned invalid translation text")
        tokens, duration = answer.get("eval_count"), answer.get("eval_duration")
        tps = round(tokens * 1e9 / duration, 2) if isinstance(tokens, int) and isinstance(duration, (int, float)) and duration > 0 else None
        results.append({
            "source": source, "target": target, "wall_seconds": round(time.monotonic() - start, 3),
            "output_tokens_per_second": tps, "nonempty": bool(response.strip()),
            "text": response,
            "quality": "NOT VERIFIED. Smoke check only; literary fidelity needs #985 reference/human evaluation.",
        })
    return results


def pull(address: str, model: str):
    if model not in MODELS:
        raise ValueError("Model not allowlisted")
    response = call(address, "/api/pull", {"model": model, "stream": True}, timeout=7200, stream=True)
    try:
        for line in response:
            if len(line) > 8192:
                raise ValueError("Pull progress event too large")
            if line.strip():
                event = json.loads(line)
                if event.get("error"):
                    raise RuntimeError("Model pull failed: " + str(event["error"])[:180])
                status = str(event.get("status", ""))[:100]
                total, done = event.get("total"), event.get("completed")
                if isinstance(total, int) and total > 0 and isinstance(done, int):
                    status += f" {min(100, round(done * 100 / total))}%"
                if status:
                    print(status, file=sys.stderr)
    finally:
        response.close()
    if model not in installed(address):
        raise RuntimeError("Ollama did not report the model installed after pull")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ollama", default=os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434"))
    parser.add_argument("--runtime-shares-memory-budget", action="store_true",
                        help="ONLY when the runtime actually shares Jularr's memory accounting; NOT merely the same physical host")
    parser.add_argument("--pull", choices=MODELS, help="Explicit model download into Ollama's persistent model volume")
    parser.add_argument("--benchmark", choices=MODELS, help="Measure an ALREADY installed model")
    parser.add_argument("--output", help="Write machine-readable JSON report")
    args = parser.parse_args(argv)
    address = endpoint(args.ollama)
    report = {"runtime": address, "hardware": hardware(args.runtime_shares_memory_budget)}
    try:
        report["installed"] = installed(address)
        report["loaded"] = loaded(address)
        if args.pull:
            pull(address, args.pull)
            report["installed"] = installed(address)
        if args.benchmark:
            if args.benchmark not in report["installed"]:
                raise ValueError("Model not installed; --pull is separate and always explicit")
            report["benchmark"] = {args.benchmark: benchmark(address, args.benchmark)}
    except (HTTPError, URLError, OSError, ValueError, RuntimeError, TimeoutError) as error:
        report["error"] = str(error)[:300]
    result = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        Path(args.output).write_text(result + "\n", encoding="utf-8")
    print(result)
    return 1 if "error" in report else 0


if __name__ == "__main__":
    sys.exit(main())
