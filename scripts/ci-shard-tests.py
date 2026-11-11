#!/usr/bin/env python3
"""Plan the backend and browser tests of a CI run as balanced shards and lanes.

Units. A test class planned at up to CLASS_LIMIT seconds is one unit and runs with
`FullyQualifiedName~NS.Class.` (its tests stay in one process). A larger class is split into
method groups of at most about GROUP_LIMIT seconds. A group names its methods exactly
(`FullyQualifiedName=NS.Class.Method`, so Foo never also runs Foo_Bar) and keeps the DataRows of a
method together. The group with the most methods takes the rest of the class (`~NS.Class.` minus the
methods of the other groups and the excluded ones), so a test the source scan did not see still runs
exactly once.

Weights. Seconds per test method are the median over the last three finished Backend Full runs on main
(`fetch` downloads their timings-* and smoke-results TRX; only passed tests count, so a red main still
measures its current code), filled up from tests/ci-durations.json.
A method without a measurement weighs as the median measured method of its class; a class without
any weighs as its stored class time, else as the median class.

Packing. Units go longest first into the lightest of shards x lanes lanes (ties by name, so every
runner computes the same plan); lane j belongs to shard j // lanes. The plan is computed once per
run (build or prepare job) and shipped to the shards in a plan file.

  ci-shard-tests.py fetch   --repo OWNER/NAME --out DIR [--runs 3]
  ci-shard-tests.py matrix  --kind K --lanes L (--shards N | --lane-limit SECONDS [--max-shards M])
                            [--classes A,B] [--exclude Class,Class.Method] [--reserve 6=150]
                            [--measured DIR] --out PLAN
                            writes the plan file and prints the job matrix {"include":[{"shard":"1/N"},...]}
  ci-shard-tests.py lanes   --plan PLAN --shard i/N     one dotnet test --filter per lane (empty line: empty lane)
  ci-shard-tests.py plan    --kind K --shard i/N [--lanes L] [--classes ..] [--exclude ..] [--reserve ..] [--measured DIR]
  ci-shard-tests.py table   --kind K --shards N [--lanes L] [--exclude ..] [--reserve ..] [--measured DIR]
  ci-shard-tests.py refresh TRX_DIR...                  one folder per run: store the median seconds per
                                                        method and class in tests/ci-durations.json
  ci-shard-tests.py --self-test
"""
from __future__ import annotations

import argparse
import io
import json
import math
import re
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parent.parent
DURATIONS = ROOT / "tests" / "ci-durations.json"
KINDS = {
    "backend": ("tests/Jularr.Backend.Tests", "Jularr.Backend.Tests"),
    "e2e": ("tests/Jularr.E2E.Tests", "Jularr.E2E.Tests"),
}
DEFAULT_WEIGHT = 10.0
CLASS_LIMIT = 150.0  # a class planned at up to this many seconds stays one unit
GROUP_LIMIT = 120.0  # a larger class is split into method groups of about this many seconds at most
FQN = "FullyQualifiedName"
# Artifacts of a Backend Full run that hold TRX files with the seconds of every test.
MEASURED_ARTIFACTS = re.compile(r"^(timings-.+|smoke-results)$")
FAILED_OUTCOMES = {"Failed", "Error", "Timeout", "Aborted"}

NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)
TEST_CLASS = re.compile(r"\[TestClass(?:\([^\]]*\))?\][\s\S]{0,300}?\bclass\s+(\w+)")
PARTIAL_CLASS = re.compile(r"\bpartial\s+class\s+(\w+)")
TEST_METHOD = re.compile(r"\[(?:Data)?TestMethod(?:\([^\]]*\))?\]")
SIGNATURE = re.compile(r"^[ \t]*(?:(?:public|internal|protected|private|static|async|virtual|override|sealed|new)\s+)+"
                       r"[^=;{}()\n]*?\b(\w+)\s*(?:<[^<>()\n]*>)?\s*\(", re.M)


# --- the tests in the source -------------------------------------------------------------------

def scan(kind: str, root: Path = ROOT) -> dict[str, list[str]]:
    """Fully qualified [TestClass] name -> its [TestMethod] names in source order (from source, no build)."""
    folder = root / KINDS[kind][0]
    files: list[tuple[str, str]] = []
    for file in sorted(folder.rglob("*.cs")):
        if {"bin", "obj"} & set(file.relative_to(folder).parts):
            continue
        text = file.read_text(encoding="utf-8-sig", errors="replace")
        namespace = NAMESPACE.search(text)
        files.append((namespace.group(1) if namespace else KINDS[kind][1], text))
    tests: dict[str, list[str]] = {}
    for namespace, text in files:
        for match in TEST_CLASS.finditer(text):
            tests.setdefault(f"{namespace}.{match.group(1)}", [])
    for namespace, text in files:
        # A method belongs to the test class declared last before it; a partial part without
        # [TestClass] counts when another part of that class has it.
        owners = sorted({(match.start(1), f"{namespace}.{match.group(1)}") for match in TEST_CLASS.finditer(text)}
                        | {(match.start(1), f"{namespace}.{match.group(1)}") for match in PARTIAL_CLASS.finditer(text)
                           if f"{namespace}.{match.group(1)}" in tests})
        for attribute in TEST_METHOD.finditer(text):
            owner = [name for position, name in owners if position < attribute.start()]
            signature = SIGNATURE.search(text, attribute.end())
            if owner and signature and signature.group(1) not in tests[owner[-1]]:
                tests[owner[-1]].append(signature.group(1))
    return dict(sorted(tests.items()))


def discover(kind: str, root: Path = ROOT) -> list[str]:
    """Fully qualified names of the [TestClass] classes of a test project."""
    return list(scan(kind, root))


# --- measured seconds --------------------------------------------------------------------------

@dataclass
class Measured:
    methods: dict[str, dict[str, float]] = field(default_factory=dict)  # class -> method -> seconds
    classes: dict[str, float] = field(default_factory=dict)             # class -> seconds
    source: str = "nothing measured"


def load_durations(kind: str, path: Path = DURATIONS) -> Measured:
    if not path.exists():
        return Measured(source=f"no {path.name}")
    document = json.loads(path.read_text(encoding="utf-8"))
    methods = document.get("methods", {}).get(kind, {})
    return Measured({name: {method: float(value) for method, value in values.items()} for name, values in methods.items()},
                    {name: float(value) for name, value in document.get(kind, {}).items()},
                    f"{path.name} of {document.get('updated', 'unknown date')}")


def seconds(value: str) -> float:
    hours, minutes, rest = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(rest)


def trx_methods(folder: Path) -> dict[str, dict[str, dict[str, float]]]:
    """kind -> class -> method -> seconds for the TRX files of one run. The DataRows of a method are
    summed per file; a method in several files (smoke and shard) counts once, with its longest time.
    A method with a failed row is no measurement (a failure can be instant or run into a timeout)."""
    result: dict[str, dict[str, dict[str, float]]] = {kind: {} for kind in KINDS}
    for file in sorted(folder.rglob("*.trx")):
        try:
            root = ET.parse(file).getroot()
        except ET.ParseError as error:
            print(f"::warning::{file} is no readable TRX ({error}); skipped.", file=sys.stderr)
            continue
        tests: dict[str, tuple[str, str]] = {}
        for unit in root.findall(".//{*}UnitTest"):
            method = unit.find("{*}TestMethod")
            if method is not None:
                tests[unit.attrib.get("id", "")] = (method.attrib.get("className", "").split(",")[0].strip(),
                                                    method.attrib.get("name", ""))
        sums: dict[tuple[str, str], float] = {}
        failed: set[tuple[str, str]] = set()
        for outcome in root.findall("./{*}Results/{*}UnitTestResult"):
            key = tests.get(outcome.attrib.get("testId", ""))
            if key and key[1]:
                sums[key] = sums.get(key, 0.0) + seconds(outcome.attrib.get("duration", "00:00:00"))
                if outcome.attrib.get("outcome") in FAILED_OUTCOMES:
                    failed.add(key)
        for (name, method), value in sums.items():
            if (name, method) in failed:
                continue
            for kind, (_, namespace) in KINDS.items():
                if name.startswith(namespace + "."):
                    stored = result[kind].setdefault(name, {})
                    stored[method] = max(stored.get(method, 0.0), value)
    return result


def run_folders(folder: Path) -> list[Path]:
    """The runs below a fetch folder: every direct subfolder holding TRX files (or the folder itself)."""
    if not folder.is_dir():
        return []
    runs = [child for child in sorted(folder.iterdir()) if child.is_dir() and any(child.rglob("*.trx"))]
    return runs or ([folder] if any(folder.glob("*.trx")) else [])


def measure(runs: list[Path]) -> dict[str, Measured]:
    """kind -> the median seconds per method over the runs that measured it."""
    per_run = [trx_methods(folder) for folder in runs]
    names = ", ".join(folder.name for folder in runs)
    result = {}
    for kind in KINDS:
        values: dict[str, dict[str, list[float]]] = {}
        for run in per_run:
            for name, methods in run[kind].items():
                for method, value in methods.items():
                    values.setdefault(name, {}).setdefault(method, []).append(value)
        result[kind] = Measured({name: {method: statistics.median(times) for method, times in methods.items()}
                                 for name, methods in values.items()}, {}, f"median of runs {names}")
    return result


def merged(primary: Measured, fallback: Measured) -> Measured:
    """The primary measurements, filled up per method and class from the fallback."""
    methods = {name: dict(values) for name, values in fallback.methods.items()}
    for name, values in primary.methods.items():
        methods.setdefault(name, {}).update(values)
    return Measured(methods, {**fallback.classes, **primary.classes}, f"{primary.source}, filled up from {fallback.source}")


def durations_for(kind: str, measured_dir: Path | None, path: Path = DURATIONS) -> Measured:
    stored = load_durations(kind, path)
    runs = run_folders(measured_dir) if measured_dir else []
    if not runs:
        if measured_dir:
            print(f"::notice::No measured runs in {measured_dir}: the plan uses {stored.source}.", file=sys.stderr)
        return stored
    return merged(measure(runs)[kind], stored)


# --- units, lanes and shards -------------------------------------------------------------------

@dataclass(frozen=True)
class Unit:
    name: str
    seconds: float
    filter: str


def weigh(tests: dict[str, list[str]], measured: Measured) -> tuple[dict[str, float], dict[str, dict[str, float]], set[str]]:
    """(class -> seconds, class -> method -> seconds, classes without any measurement) for every class."""
    totals: dict[str, float] = {}
    per_method: dict[str, dict[str, float]] = {}
    unmeasured: set[str] = set()
    for name, methods in tests.items():
        known = {method: value for method, value in measured.methods.get(name, {}).items() if method in methods}
        if known:
            fill = statistics.median(known.values())
            per_method[name] = {method: known.get(method, fill) for method in methods}
            totals[name] = sum(per_method[name].values())
        elif name in measured.classes:
            totals[name] = measured.classes[name]
        elif not methods and measured.methods.get(name):
            totals[name] = sum(measured.methods[name].values())
        else:
            unmeasured.add(name)
    unknown = statistics.median(totals.values()) if totals else DEFAULT_WEIGHT
    for name in unmeasured:
        totals[name] = unknown
    for name, methods in tests.items():
        if name not in per_method:
            per_method[name] = {method: totals[name] / len(methods) for method in methods}
    return totals, per_method, unmeasured


def resolve(names: list[str], classes: list[str], kind: str) -> list[str]:
    """Short or fully qualified class names -> the fully qualified classes of the project."""
    wanted: set[str] = set()
    for name in names:
        matches = [full for full in classes if full == name or full.endswith("." + name)]
        if not matches:
            print(f"::warning::Test class {name} is not in {KINDS[kind][0]}; skipped.", file=sys.stderr)
        wanted.update(matches)
    return sorted(wanted)


def parse_exclude(items: list[str], tests: dict[str, list[str]], kind: str) -> tuple[set[str], dict[str, set[str]]]:
    """`Class` or `Class.Method` (short or fully qualified class) -> (classes, class -> methods) left out."""
    classes: set[str] = set()
    methods: dict[str, set[str]] = {}
    for item in items:
        if not item:
            continue
        full = resolve([item], list(tests), kind) if any(name.endswith("." + item) or name == item for name in tests) else []
        if full:
            classes.update(full)
            continue
        owner, _, method = item.rpartition(".")
        found = resolve([owner], list(tests), kind) if owner else []
        if not found:
            raise SystemExit(f"::error::--exclude {item} names no test class of {KINDS[kind][0]}.")
        for name in found:
            methods.setdefault(name, set()).add(method)
    return classes, methods


def class_filter(name: str, excluded: set[str] | frozenset[str] = frozenset()) -> str:
    if not excluded:
        return f"{FQN}~{name}."
    return "(" + "&".join([f"{FQN}~{name}."] + [f"{FQN}!={name}.{method}" for method in sorted(excluded)]) + ")"


def method_filter(name: str, methods: list[str]) -> str:
    return "|".join(f"{FQN}={name}.{method}" for method in sorted(methods))


def units_of(kind: str, tests: dict[str, list[str]], measured: Measured, only: list[str] | None = None,
             exclude: list[str] | None = None) -> tuple[list[Unit], set[str]]:
    """(the planned units, the planned classes without any measurement)."""
    classes = list(tests) if only is None else resolve(only, list(tests), kind)
    excluded_classes, excluded_methods = parse_exclude(exclude or [], tests, kind)
    totals, per_method, unmeasured = weigh(tests, measured)
    # A [TestClass] in which the scan sees no test method and that never ran (an assembly fixture holder, or
    # tests the scan cannot read, such as inherited ones) still runs as a whole class, planned at 0 s.
    hollow = {name for name in unmeasured if not tests[name]}
    units: list[Unit] = []
    for name in classes:
        if name in excluded_classes:
            continue
        dropped = excluded_methods.get(name, set())
        weights = {method: value for method, value in per_method[name].items() if method not in dropped}
        total = sum(weights.values()) if tests[name] else 0.0 if name in hollow else totals[name]
        if total <= CLASS_LIMIT or len(weights) < 2:
            units.append(Unit(name, total, class_filter(name, dropped)))
            continue
        # Methods measured at 0 s can leave a bin of the packing empty: an empty group is no unit.
        groups = [group for group in pack(weights, min(len(weights), math.ceil(total / GROUP_LIMIT))) if group]
        rest = max(range(len(groups)), key=lambda index: (len(groups[index]), -index))
        taken = set(dropped).union(*(group for index, group in enumerate(groups) if index != rest))
        for index, group in enumerate(groups):
            units.append(Unit(f"{name}[{index + 1}/{len(groups)}]", sum(weights[method] for method in group),
                              class_filter(name, taken) if index == rest else method_filter(name, group)))
    return units, (unmeasured - hollow) & set(name for name in classes if name not in excluded_classes)


def pack(items: dict[str, float], bins: int, reserve: dict[int, float] | None = None) -> list[list[str]]:
    """Greedy longest-first packing; deterministic (weight desc, name asc; lightest bin, lowest index)."""
    loads = [float((reserve or {}).get(index, 0)) for index in range(bins)]
    result: list[list[str]] = [[] for _ in range(bins)]
    for name, weight in sorted(items.items(), key=lambda item: (-item[1], item[0])):
        index = min(range(bins), key=lambda candidate: (loads[candidate], candidate))
        result[index].append(name)
        loads[index] += weight
    return [sorted(names) for names in result]


def plan_lanes(units: list[Unit], shards: int, lanes: int, reserve: dict[int, float] | None = None) -> list[list[list[Unit]]]:
    """shards -> lanes -> units. `reserve` (shard index -> seconds) is spread over that shard's lanes."""
    lane_reserve = {shard * lanes + lane: value / lanes for shard, value in (reserve or {}).items() for lane in range(lanes)}
    by_name = {unit.name: unit for unit in units}
    packed = pack({unit.name: unit.seconds for unit in units}, shards * lanes, lane_reserve)
    return [[[by_name[name] for name in packed[shard * lanes + lane]] for lane in range(lanes)] for shard in range(shards)]


def lane_seconds(lane: list[Unit]) -> float:
    return sum(unit.seconds for unit in lane)


def lane_filter(lane: list[Unit]) -> str:
    return "|".join(unit.filter for unit in lane)


def auto_shards(units: list[Unit], lanes: int, lane_limit: float, max_shards: int,
                reserve: dict[int, float] | None = None) -> int:
    """The fewest shards (1..max_shards) whose slowest planned lane stays within lane_limit."""
    total = sum(unit.seconds for unit in units)
    # Every shard gets at least one unit: the first units go one per lane, in lane order.
    most = max(1, min(max_shards, math.ceil(len(units) / lanes)))
    count = max(1, min(most, math.ceil(total / (lanes * lane_limit))))
    while count < most and max(map(lane_seconds, sum(plan_lanes(units, count, lanes, reserve), []))) > lane_limit:
        count += 1
    return count


def shards_of(kind: str, shards: int, lanes: int = 1, only: list[str] | None = None,
              reserve: dict[int, float] | None = None, root: Path = ROOT,
              durations: Measured | None = None, exclude: list[str] | None = None) -> list[list[list[Unit]]]:
    tests = scan(kind, root)
    units, _ = units_of(kind, tests, load_durations(kind) if durations is None else durations, only, exclude)
    return plan_lanes(units, shards, lanes, reserve)


def plan_document(kind: str, plan: list[list[list[Unit]]], source: str, unmeasured: set[str]) -> dict:
    return {
        "kind": kind,
        "shards": len(plan),
        "lanes": len(plan[0]) if plan else 0,
        "durations": source,
        "unmeasured": sorted(unmeasured),
        "seconds": round(sum(lane_seconds(lane) for shard in plan for lane in shard), 1),
        "plan": [[{"seconds": round(lane_seconds(lane), 1), "units": [unit.name for unit in lane], "filter": lane_filter(lane)}
                  for lane in shard] for shard in plan],
    }


def describe(document: dict) -> str:
    lines = [f"{document['kind']}: {document['shards']} shard(s) x {document['lanes']} lane(s), "
             f"{document['seconds']:.0f} s planned. Durations: {document['durations']}.",
             f"{len(document['unmeasured'])} planned {document['kind']} classes without a measurement"
             + (f": {', '.join(document['unmeasured'])}" if document["unmeasured"] else ".")]
    for index, shard in enumerate(document["plan"], start=1):
        lines.append(f"  shard {index}/{document['shards']}: " + " / ".join(
            f"{lane['seconds']:.0f} s ({len(lane['units'])} units)" for lane in shard))
    return "\n".join(lines)


def parse_shard(text: str) -> tuple[int, int]:
    index, _, count = text.partition("/")
    shard, shards = int(index), int(count)
    if not 1 <= shard <= shards:
        raise SystemExit(f"Shard {text} is out of range (use 1/{shards} to {shards}/{shards}).")
    return shard, shards


def parse_reserve(items: list[str]) -> dict[int, float]:
    return {int(key) - 1: float(value) for key, _, value in (item.partition("=") for item in items)}


def split_list(text: str | None) -> list[str] | None:
    return None if text is None else [name.strip() for name in text.split(",") if name.strip()]


# --- the measured runs on GitHub ---------------------------------------------------------------

def gh_api(path: str) -> bytes:
    return subprocess.run(["gh", "api", path], capture_output=True, check=True, timeout=120).stdout


def fetch(repo: str, out: Path, runs: int = 3, api: Callable[[str], bytes] = gh_api,
          workflow: str = "backend.yml", branch: str = "main") -> list[str]:
    """Download the TRX files of the last `runs` finished Backend Full runs on main (green or red, not
    cancelled) that kept timings into out/RUN_ID/ARTIFACT/. Red runs count: trx_methods takes passed tests
    only, and a red main would otherwise freeze the plan on old code. Returns the run IDs that delivered
    TRX files; any failure only narrows that list."""
    out.mkdir(parents=True, exist_ok=True)
    try:
        listing = json.loads(api(f"repos/{repo}/actions/workflows/{workflow}/runs?branch={branch}&status=completed&per_page=20"))
    except (OSError, subprocess.SubprocessError, ValueError) as error:
        print(f"::warning::Listing the finished {workflow} runs failed ({error}).", file=sys.stderr)
        return []
    downloads: list[tuple[str, str, int]] = []
    chosen = 0
    for run in listing.get("workflow_runs", []):
        if chosen == runs:
            break
        if run.get("conclusion") not in ("success", "failure"):
            continue
        try:
            artifacts = json.loads(api(f"repos/{repo}/actions/runs/{run['id']}/artifacts?per_page=100")).get("artifacts", [])
        except (OSError, subprocess.SubprocessError, ValueError) as error:
            print(f"::warning::Listing the artifacts of run {run['id']} failed ({error}).", file=sys.stderr)
            continue
        measured = [(str(run["id"]), artifact["name"], artifact["id"]) for artifact in artifacts
                    if MEASURED_ARTIFACTS.match(artifact.get("name", "")) and not artifact.get("expired")]
        if measured:
            downloads += measured
            chosen += 1

    def download(item: tuple[str, str, int]) -> str:
        run_id, name, artifact_id = item
        target = out / run_id / re.sub(r"[^\w.-]", "_", name)
        with zipfile.ZipFile(io.BytesIO(api(f"repos/{repo}/actions/artifacts/{artifact_id}/zip"))) as archive:
            for member in archive.infolist():
                if member.filename.endswith(".trx") and member.file_size < 64 * 1024 * 1024:
                    target.mkdir(parents=True, exist_ok=True)
                    (target / Path(member.filename).name).write_bytes(archive.read(member))
        return run_id

    delivered: set[str] = set()
    with ThreadPoolExecutor(max_workers=16) as pool:
        futures = {pool.submit(download, item): item for item in downloads}
        for future in as_completed(futures):
            try:
                delivered.add(future.result())
            except (OSError, subprocess.SubprocessError, zipfile.BadZipFile, ValueError) as error:
                print(f"::warning::Artifact {futures[future][1]} of run {futures[future][0]} could not be read ({error}).",
                      file=sys.stderr)
    return sorted(run_id for run_id in delivered if any((out / run_id).rglob("*.trx")))


# --- the committed fallback --------------------------------------------------------------------

def refresh(folders: list[Path], path: Path = DURATIONS, root: Path = ROOT) -> int:
    document = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    measured = measure(folders)
    stored_methods = document.get("methods", {})
    result: dict = {}
    methods_out: dict = {}
    changed = 0
    for kind in KINDS:
        tests = scan(kind, root)
        stored = {name: dict(values) for name, values in stored_methods.get(kind, {}).items()}
        for name, values in measured[kind].methods.items():
            stored.setdefault(name, {}).update({method: round(value, 2) for method, value in values.items()})
            changed += len(values)
        live: dict[str, dict[str, float]] = {}
        for name in sorted(stored):
            if name in tests:
                values = {method: value for method, value in sorted(stored[name].items()) if not tests[name] or method in tests[name]}
                if values:
                    live[name] = values
        classes = {name: value for name, value in document.get(kind, {}).items() if name in tests}
        classes.update({name: round(sum(values.values()), 1) for name, values in live.items()})
        result[kind] = dict(sorted(classes.items()))
        methods_out[kind] = live
    result["methods"] = methods_out
    result["updated"] = date.today().isoformat()
    path.write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
    print(f"Stored {changed} method durations (median of {len(folders)} run(s)) in {path.name}.")
    return 0


def table(kind: str, shards: int, lanes: int, reserve: dict[int, float] | None = None,
          measured_dir: Path | None = None, exclude: list[str] | None = None) -> str:
    measured = durations_for(kind, measured_dir)
    units, unmeasured = units_of(kind, scan(kind), measured, exclude=exclude)
    rows = ["| Shard | Units | Expected test time | Lane times |", "| ---: | ---: | ---: | --- |"]
    for index, lane_plan in enumerate(plan_lanes(units, shards, lanes, reserve), start=1):
        totals = [lane_seconds(lane) for lane in lane_plan]
        rows.append(f"| {index}/{shards} | {sum(len(lane) for lane in lane_plan)} | {max(totals):.0f} s | "
                    + " / ".join(f"{total:.0f} s" for total in totals) + " |")
    split = sorted({unit.name.partition("[")[0] for unit in units if "[" in unit.name})
    rows.append(f"\n{len(units)} units, {sum(unit.seconds for unit in units):.0f} s planned, {len(unmeasured)} classes "
                f"without a measurement. Split by method: {', '.join(split) or 'none'}. Durations: {measured.source}.")
    return "\n".join(rows)


# --- self-test ---------------------------------------------------------------------------------

def compile_filter(expression: str) -> Callable[[str], bool]:
    """A dotnet test --filter expression over FullyQualifiedName, evaluated like VSTest (case-insensitive)."""
    tokens = re.findall(rf"\(|\)|\||&|{FQN}(?:!=|!~|=|~)[^|&()]*", expression)
    assert "".join(tokens) == expression, f"unparsable filter: {expression[:200]}"
    position = 0

    def parse(level: int) -> Callable[[str], bool]:
        nonlocal position
        if level == 2:
            token = tokens[position]
            position += 1
            if token == "(":
                inner = parse(0)
                assert tokens[position] == ")", expression
                position += 1
                return inner
            operator, operand = re.fullmatch(rf"{FQN}(!=|!~|=|~)(.*)", token).groups()
            operand = operand.lower()
            return {"=": lambda name: name == operand, "!=": lambda name: name != operand,
                    "~": lambda name: operand in name, "!~": lambda name: operand not in name}[operator]
        parts = [parse(level + 1)]
        separator = "|" if level == 0 else "&"
        while position < len(tokens) and tokens[position] == separator:
            position += 1
            parts.append(parse(level + 1))
        if level == 0:
            return lambda name: any(part(name) for part in parts)
        return lambda name: all(part(name) for part in parts)

    predicate = parse(0)
    assert position == len(tokens), expression
    return lambda name: predicate(name.lower())


def assert_exactly_once(units: list[Unit], tests: list[str], absent: set[str] = frozenset()) -> None:
    """Every test matches exactly one unit; the `absent` tests match none."""
    predicates = [(unit.name, compile_filter(unit.filter)) for unit in units]
    for test in tests:
        hits = [name for name, predicate in predicates if predicate(test)]
        if test in absent:
            assert not hits, (test, hits)
        else:
            assert len(hits) == 1, (test, hits)


def self_test() -> None:
    import contextlib
    import random
    import tempfile

    items = {f"C{i}": float(w) for i, w in enumerate([240, 150, 100, 50, 49, 45, 40, 38, 35, 30, 20, 10, 5, 5, 1])}
    for bins in (1, 2, 4, 7):
        packed = pack(items, bins)
        flat = [name for shard in packed for name in shard]
        assert sorted(flat) == sorted(items) and len(flat) == len(set(flat)), "every unit exactly once"
        loads = [sum(items[name] for name in shard) for shard in packed]
        assert max(loads) <= max(max(items.values()), sum(items.values()) / bins * 4 / 3 + 1), loads
    assert pack(items, 4) == pack(dict(reversed(list(items.items()))), 4), "independent of input order"
    assert pack({"A": 1, "B": 1}, 2, {0: 50}) == [[], ["A", "B"]], "reserved time is honoured"
    assert parse_shard("3/4") == (3, 4) and parse_reserve(["1=75"]) == {0: 75.0}
    assert class_filter("N.A") == "FullyQualifiedName~N.A."
    assert class_filter("N.A", {"Y", "X"}) == "(FullyQualifiedName~N.A.&FullyQualifiedName!=N.A.X&FullyQualifiedName!=N.A.Y)"
    assert method_filter("N.A", ["B", "A"]) == "FullyQualifiedName=N.A.A|FullyQualifiedName=N.A.B"
    assert seconds("00:01:02.5") == 62.5
    match = compile_filter("FullyQualifiedName=N.C.Foo|(FullyQualifiedName~N.D.&FullyQualifiedName!=N.D.Bar)")
    assert match("N.C.Foo") and match("n.c.foo") and not match("N.C.Foo_Bar") and match("N.D.Baz") and not match("N.D.Bar")

    # weights: a method without a measurement weighs as the median method of its class; a class without
    # any as its stored class time, else as the median class
    tests = {"N.A": ["a1", "a2", "a3"], "N.B": ["b1"], "N.C": ["c1", "c2"], "N.New": ["n1"]}
    totals, per_method, unmeasured = weigh(tests, Measured({"N.A": {"a1": 2, "a2": 4, "gone": 99}}, {"N.B": 30}))
    assert per_method["N.A"] == {"a1": 2, "a2": 4, "a3": 3} and totals["N.A"] == 9
    assert totals["N.B"] == 30 and totals["N.C"] == totals["N.New"] == statistics.median([9, 30])
    assert per_method["N.C"] == {"c1": totals["N.C"] / 2, "c2": totals["N.C"] / 2} and unmeasured == {"N.C", "N.New"}
    assert weigh({"Only": []}, Measured())[0]["Only"] == DEFAULT_WEIGHT

    # units: a large class is split by method; exact clauses keep Foo apart from Foo_Bar; the rest group
    # also takes a method the source scan missed; excluded methods and classes run nowhere
    big = [f"M{i:02}" for i in range(30)] + ["Foo", "Foo_Bar"]
    tests = {"N.Big": big, "N.Small": ["S1", "S2"], "N.Boot": ["B1"], "N.Mixed": ["Keep", "Smoke"]}
    measured = Measured({"N.Big": {**{name: 20.0 for name in big}, "Foo": 61.0, "Foo_Bar": 59.0}, "N.Small": {"S1": 1, "S2": 1}})
    units, _ = units_of("backend", tests, measured, exclude=["N.Boot", "N.Mixed.Smoke"])
    big_units = [unit for unit in units if unit.name.startswith("N.Big[")]
    assert len(big_units) == math.ceil((30 * 20 + 120) / GROUP_LIMIT), [unit.name for unit in big_units]
    assert all(unit.seconds <= GROUP_LIMIT + 20 for unit in big_units), [unit.seconds for unit in big_units]
    names = [f"N.Big.{name}" for name in big] + ["N.Big.Unseen", "N.Small.S1", "N.Small.S2", "N.Boot.B1",
                                                 "N.Mixed.Keep", "N.Mixed.Smoke", "N.Mixed.Smoke_More"]
    assert_exactly_once(units, names, absent={"N.Boot.B1", "N.Mixed.Smoke"})
    hollow, unknown = units_of("backend", {"N.Fixture": [], "N.T": ["M"]}, Measured())
    assert [(unit.name, unit.seconds) for unit in hollow] == [("N.Fixture", 0.0), ("N.T", DEFAULT_WEIGHT)] and unknown == {"N.T"}, \
        "a class without seen tests still runs, at 0 s"
    assert_exactly_once(hollow, ["N.Fixture.Inherited", "N.T.M"])
    # methods measured at 0 s never leave a method group empty
    lopsided, _ = units_of("backend", {"N.L": ["Long", "Quick", "Skipped"]},
                           Measured({"N.L": {"Long": 300.0, "Quick": 0.0, "Skipped": 0.0}}))
    assert len(lopsided) == 2 and all(unit.filter for unit in lopsided), lopsided
    assert_exactly_once(lopsided, ["N.L.Long", "N.L.Quick", "N.L.Skipped", "N.L.Unseen"])
    for shards, lanes in ((1, 1), (2, 2), (3, 2), (5, 1)):
        plan = plan_lanes(units, shards, lanes)
        lane_units = [Unit(f"lane{index}", 0, lane_filter(lane) or "FullyQualifiedName=none")
                      for index, lane in enumerate(lane for shard in plan for lane in shard)]
        assert_exactly_once(lane_units, names, absent={"N.Boot.B1", "N.Mixed.Smoke"})
    shuffled = dict(random.Random(7).sample(list(tests.items()), len(tests)))
    assert plan_lanes(units_of("backend", shuffled, measured)[0], 3, 2) == plan_lanes(units_of("backend", tests, measured)[0], 3, 2)
    assert plan_lanes(units, 1, 2, {0: 1000}) == plan_lanes(units, 1, 2, {0: 1000}), "deterministic"
    # the shard count grows with the selected time until the slowest lane fits (at most max_shards)
    light = [Unit(f"U{i}", 100, f"FullyQualifiedName~N.U{i}.") for i in range(20)]
    assert auto_shards(light[:2], 2, 330, 6) == 1 and auto_shards(light, 2, 330, 6) == 4 and auto_shards(light * 5, 2, 330, 6) == 6
    assert auto_shards([], 2, 330, 6) == 1
    assert auto_shards([Unit("Long", 900, "FullyQualifiedName=N.L.M"), Unit("Short", 1, "FullyQualifiedName~N.S.")], 2, 330, 6) == 1, \
        "no shard without a unit"

    # TRX: DataRows of a method summed, a method in two files of one run once, median over runs
    def trx(rows: list[tuple[str, str, str]], failed: set[int] = frozenset()) -> str:
        units_xml = "".join(f'<UnitTest id="{index}"><TestMethod className="{cls}" name="{method}"/></UnitTest>'
                            for index, (cls, method, _) in enumerate(rows))
        results = "".join(f'<UnitTestResult testId="{index}" duration="{duration}" outcome="{"Failed" if index in failed else "Passed"}"/>'
                          for index, (_, _, duration) in enumerate(rows))
        return f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results><TestDefinitions>{units_xml}</TestDefinitions></TestRun>'

    with tempfile.TemporaryDirectory() as folder:
        base = Path(folder)
        row = "Jularr.Backend.Tests.RowTests"
        for run, (first, second) in {"1": ("00:00:01", "00:00:02"), "2": ("00:00:03", "00:00:04"), "3": ("00:00:10", "00:00:10")}.items():
            (base / run / "timings-backend-0").mkdir(parents=True)
            (base / run / "timings-backend-0" / "lane1.trx").write_text(trx([(row, "Data", first), (row, "Data", second), (row, "One", first),
                                                                             (row, "Broken", first), (row, "Broken", second)], failed={4}))
            (base / run / "smoke-results").mkdir()
            (base / run / "smoke-results" / "smoke.trx").write_text(trx([(row, "One", "00:00:00.5")]))
        (base / "4").mkdir()  # a run without TRX files is no run
        assert [folder.name for folder in run_folders(base)] == ["1", "2", "3"]
        result = measure(run_folders(base))["backend"]
        assert result.methods[row] == {"Data": 7.0, "One": 3.0}, "DataRows summed, a failed method no measurement"
        filled = merged(result, Measured({row: {"Old": 5.0}}, {"Jularr.Backend.Tests.Other": 4.0}, "file"))
        assert filled.methods[row] == {"Data": 7.0, "One": 3.0, "Old": 5.0} and filled.classes == {"Jularr.Backend.Tests.Other": 4.0}

        # fetch: three green runs, their timings and smoke artifacts; a failing API falls back to nothing
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w") as zipped:
            zipped.writestr("backend-lane1.trx", trx([(row, "Data", "00:00:02")]))
            zipped.writestr("notes.txt", "ignored")
        # the last three finished runs with timings: red ones too, never cancelled ones or runs without timings
        runs_listed = {"workflow_runs": [{"id": 11, "conclusion": "success"}, {"id": 12, "conclusion": "cancelled"},
                                         {"id": 13, "conclusion": "failure"}, {"id": 14, "conclusion": "success"},
                                         {"id": 15, "conclusion": "success"}, {"id": 16, "conclusion": "success"}]}
        artifacts = {"artifacts": [{"id": 1, "name": "timings-backend-0"}, {"id": 2, "name": "jularr-build"},
                                   {"id": 3, "name": "smoke-results"}, {"id": 4, "name": "timings-e2e-6", "expired": True}]}
        requested: list[str] = []

        def fake_api(path: str) -> bytes:
            requested.append(path)
            if path.endswith("/zip"):
                return archive.getvalue()
            if "/runs/14/artifacts?" in path:
                return json.dumps({"artifacts": [{"id": 9, "name": "jularr-build"}]}).encode()
            if "/artifacts?" in path:
                return json.dumps(artifacts).encode()
            assert "status=completed" in path, path
            return json.dumps(runs_listed).encode()

        target = base / "fetched"
        assert fetch("o/r", target, 3, fake_api) == ["11", "13", "15"]
        assert not any("/runs/12/" in path or "/runs/16/" in path for path in requested), requested
        assert sorted(path for path in requested if path.endswith("/zip")) == ["repos/o/r/actions/artifacts/1/zip"] * 3 + ["repos/o/r/actions/artifacts/3/zip"] * 3
        assert not list(target.rglob("*.txt")) and len(list(target.rglob("*.trx"))) == 6
        assert durations_for("backend", target, base / "missing.json").methods[row] == {"Data": 2.0}

        def broken_api(path: str) -> bytes:
            raise subprocess.CalledProcessError(1, ["gh", "api", path], stderr=b"HTTP 403")

        with contextlib.redirect_stderr(io.StringIO()) as log:
            assert fetch("o/r", base / "none", 3, broken_api) == []
            assert durations_for("backend", base / "none").source == load_durations("backend").source, "fallback to the committed file"
        assert "Listing the finished backend.yml runs failed" in log.getvalue() and "uses ci-durations.json" in log.getvalue()
        assert durations_for("backend", None).source == load_durations("backend").source

    # discovery against a temporary tree: block and file-scoped namespaces, attributes, partial parts,
    # nested helpers, DataRows, non-test classes
    with tempfile.TemporaryDirectory() as folder:
        project = Path(folder) / KINDS["backend"][0]
        (project / "obj").mkdir(parents=True)
        (project / "A.cs").write_text(
            "namespace X.Y\n{\n    [TestClass]\n    public sealed class AlphaTests\n    {\n"
            "        [TestMethod]\n        public void First() { }\n"
            "        private sealed class Helper { public void NotATest() { } }\n"
            "        [TestMethod]\n        [DataRow(\"a(b\", 1)]\n        [DataRow(\"[c]\", 2)]\n"
            "        public async Task Second_WithRows(string text, int n) { await Task.Yield(); }\n"
            "        [TestMethod]\n        [Timeout(1000)]\n        public static async Task<int> Third() => 1;\n    }\n"
            "    public class Helper { public void NotATest() { } }\n}\n")
        (project / "B.cs").write_text("namespace X.Z;\n[TestClass]\n[DoNotParallelize]\npublic sealed partial class BetaTests\n{\n"
                                      "    [TestMethod]\n    public void InB() { }\n}\n")
        (project / "B2.cs").write_text("namespace X.Z;\npublic sealed partial class BetaTests\n{\n    [TestMethod]\n    public void InB2() { }\n}\n")
        (project / "obj" / "C.cs").write_text("namespace Gen;\n[TestClass]\npublic class Ignored { [TestMethod] public void T() { } }\n")
        found = scan("backend", Path(folder))
        assert found == {"X.Y.AlphaTests": ["First", "Second_WithRows", "Third"], "X.Z.BetaTests": ["InB", "InB2"]}, found
        assert discover("backend", Path(folder)) == ["X.Y.AlphaTests", "X.Z.BetaTests"]
        only = shards_of("backend", 2, 1, only=["BetaTests"], root=Path(folder), durations=Measured())
        assert [unit.name for shard in only for lane in shard for unit in lane] == ["X.Z.BetaTests"]

    # the real test projects: every test of the source lands in exactly one unit and one lane
    stored = json.loads(DURATIONS.read_text(encoding="utf-8"))
    for kind in KINDS:
        tests = scan(kind)
        assert len(tests) > 20, f"found only {len(tests)} {kind} test classes"
        source_methods = 0
        for file in (ROOT / KINDS[kind][0]).rglob("*.cs"):
            if not {"bin", "obj"} & set(file.relative_to(ROOT / KINDS[kind][0]).parts):
                source_methods += len(TEST_METHOD.findall(file.read_text(encoding="utf-8-sig", errors="replace")))
        assert sum(map(len, tests.values())) == source_methods, f"{kind}: the scan missed a [TestMethod]"
        names = [f"{name}.{method}" for name, methods in tests.items() for method in methods]
        units, _ = units_of(kind, tests, load_durations(kind))
        assert_exactly_once(units, names)
        assert all(isinstance(value, (int, float)) and value >= 0 for value in stored[kind].values())
        assert all(value >= 0 for values in stored["methods"][kind].values() for value in values.values())
        for shards, lanes in ((6, 2), (4, 1)):
            plan = plan_lanes(units, shards, lanes)
            flat = [unit.name for shard in plan for lane in shard for unit in lane]
            assert sorted(flat) == sorted(unit.name for unit in units) and len(flat) == len(set(flat)), (kind, shards, lanes)
            assert plan == plan_lanes(list(reversed(units)), shards, lanes), "the plan is deterministic"

    # Backend Full: the smoke job (ci-select-backend-tests.py --boot-filter), the real-tool step (--tools-filter)
    # and the backend shards (planned with --exclude of its --full-exclude) run every backend test exactly once.
    # The PR shards (--pr-exclude) leave out the real-tool and the Full-only tests.
    select = [sys.executable, str(ROOT / "scripts" / "ci-select-backend-tests.py")]

    def listing(option: str) -> str:
        return subprocess.run(select + [option], capture_output=True, text=True, check=True).stdout.strip()

    tests = scan("backend")
    names = [f"{name}.{method}" for name, methods in tests.items() for method in methods]
    smoke, tools = compile_filter(listing("--boot-filter")), compile_filter(listing("--tools-filter"))
    smoked = {name for name in names if smoke(name)}
    tooled = {name for name in names if tools(name)}
    full_only = {f"{KINDS['backend'][1]}.{unit}" for unit in listing("--pr-exclude").split(",")} - tooled
    assert len(tooled) == listing("--tools-filter").count("|") + 1, "every real-tool test names one test of the source"
    assert full_only and full_only <= set(names), f"a Full-only test is not in the source: {sorted(full_only - set(names))}"
    assert not smoked & tooled
    units, _ = units_of("backend", tests, load_durations("backend"), exclude=listing("--full-exclude").split(","))
    assert len(smoked) >= len(listing("--full-exclude").split(",")) - len(tooled), "the smoke filter runs every boot class and method"
    assert_exactly_once(units, names, absent=smoked | tooled)
    pr_units, _ = units_of("backend", tests, load_durations("backend"), exclude=listing("--pr-exclude").split(","))
    assert_exactly_once(pr_units, names, absent=tooled | full_only)
    # A test that reads JULARR_TEST_FFMPEG or JULARR_TEST_CJXL runs only with the tools: it must be a real-tool test.
    for file in sorted((ROOT / KINDS["backend"][0]).rglob("*Tests.cs")):
        text = file.read_text(encoding="utf-8-sig", errors="replace")
        attributes = [match.end() for match in TEST_METHOD.finditer(text)] + [len(text)]
        for start, end in zip(attributes, attributes[1:]):
            signature = SIGNATURE.search(text, start)
            if signature and re.search(r'"JULARR_TEST_(FFMPEG|CJXL)"', text[start:end]):
                owner = [match.group(1) for match in TEST_CLASS.finditer(text) if match.start(1) < start]
                assert owner and f"{KINDS['backend'][1]}.{owner[-1]}.{signature.group(1)}" in tooled, \
                    f"{file.name}: {signature.group(1)} needs the real tools; list it in REAL_TOOLS of ci-select-backend-tests.py"

    # every shard matrix in the workflows is complete (1/N ... N/N) and planned with the same N
    seen = 0
    for workflow in sorted((ROOT / ".github" / "workflows").glob("*.yml")):
        text = workflow.read_text(encoding="utf-8")
        entries: dict[str, list[tuple[int, int]]] = {}
        for kind, index, count in re.findall(r"\{\s*kind:\s*(\w+),\s*shard:\s*(\d+)/(\d+)[^}]*\}", text):
            entries.setdefault(kind, []).append((int(index), int(count)))
        for kind, listed in entries.items():
            count = listed[0][1]
            assert listed == [(index, count) for index in range(1, count + 1)], (workflow.name, kind, listed)
            planned = re.findall(rf"ci-shard-tests\.py matrix --kind {kind}\b[^\n]*?--shards (\d+)", text)
            assert planned == [str(count)], (workflow.name, kind, planned, count)
            seen += 1
    assert seen >= 2, "the workflows no longer list their shards in the checked form"


# --- command line ------------------------------------------------------------------------------

def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        print("Test shard self-checks passed.")
        return 0
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    def selection(command: argparse.ArgumentParser) -> None:
        command.add_argument("--kind", choices=KINDS, required=True)
        command.add_argument("--lanes", type=int, default=1)
        command.add_argument("--classes", help="comma separated class names; restricts the plan to them")
        command.add_argument("--exclude", help="comma separated Class or Class.Method left out of the plan")
        command.add_argument("--reserve", action="append", default=[], help="SHARD=SECONDS already spent on that shard")
        command.add_argument("--measured", type=Path, help="folder of fetched runs (one subfolder of TRX files per run)")

    get = commands.add_parser("fetch")
    get.add_argument("--repo", required=True)
    get.add_argument("--out", type=Path, required=True)
    get.add_argument("--runs", type=int, default=3)
    matrix = commands.add_parser("matrix")
    selection(matrix)
    matrix.add_argument("--shards", type=int, help="a fixed shard count")
    matrix.add_argument("--lane-limit", type=float, help="the fewest shards whose slowest lane fits in these seconds")
    matrix.add_argument("--max-shards", type=int, default=6)
    matrix.add_argument("--out", type=Path, required=True)
    lanes = commands.add_parser("lanes")
    lanes.add_argument("--plan", type=Path, required=True)
    lanes.add_argument("--shard", required=True, help="i/N")
    plan = commands.add_parser("plan")
    selection(plan)
    plan.add_argument("--shard", required=True, help="i/N")
    show = commands.add_parser("table")
    selection(show)
    show.add_argument("--shards", type=int, required=True)
    merge = commands.add_parser("refresh")
    merge.add_argument("folders", nargs="+", type=Path)
    args = parser.parse_args()

    if args.command == "fetch":
        runs = fetch(args.repo, args.out, args.runs)
        if runs:
            print(f"Durations: median of the Backend Full runs {', '.join(runs)} on main (passed tests).")
        else:
            print(f"::notice::No Backend Full run with timings found: the plan uses {DURATIONS.relative_to(ROOT)}.")
        return 0
    if args.command == "lanes":
        document = json.loads(args.plan.read_text(encoding="utf-8"))
        shard, shards = parse_shard(args.shard)
        if shards != document["shards"]:
            raise SystemExit(f"::error::{args.plan} plans {document['shards']} {document['kind']} shards; this job is {args.shard}.")
        for lane in document["plan"][shard - 1]:
            print(lane["filter"])
        print(f"Shard {args.shard}: " + " / ".join(f"{lane['seconds']:.0f} s planned in {len(lane['units'])} units"
                                                  for lane in document["plan"][shard - 1])
              + f" ({document['durations']}).", file=sys.stderr)
        return 0
    if args.command == "refresh":
        return refresh(args.folders)

    reserve = parse_reserve(args.reserve)
    exclude = split_list(args.exclude) or []
    if args.command == "table":
        print(table(args.kind, args.shards, args.lanes, reserve, args.measured, exclude))
        return 0
    measured = durations_for(args.kind, args.measured)
    units, unmeasured = units_of(args.kind, scan(args.kind), measured, split_list(args.classes), exclude)
    if args.command == "plan":
        shard, shards = parse_shard(args.shard)
        for lane in plan_lanes(units, shards, args.lanes, reserve)[shard - 1]:
            print(lane_filter(lane))
        return 0
    if (args.shards is None) == (args.lane_limit is None):
        parser.error("matrix needs exactly one of --shards and --lane-limit")
    count = args.shards or auto_shards(units, args.lanes, args.lane_limit, args.max_shards, reserve)
    document = plan_document(args.kind, plan_lanes(units, count, args.lanes, reserve), measured.source, unmeasured)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(document, indent=1) + "\n", encoding="utf-8")
    print(describe(document), file=sys.stderr)
    print(json.dumps({"include": [{"shard": f"{index}/{count}"} for index in range(1, count + 1)]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
