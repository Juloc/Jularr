#!/usr/bin/env python3
"""Split the backend and browser test classes into balanced CI shards.

Every [TestClass] is a unit (an MSTest class keeps its fixtures together). Units are
bin-packed greedily (longest first, into the lightest shard) by their measured
duration in tests/ci-durations.json. A class without a measurement weighs as much
as the median measured class, ties break by class name, so the split is the same on
every runner. Each shard can be divided again into parallel lanes (processes).

  ci-shard-tests.py plan    --kind backend --shard 2/4 [--lanes 2] [--classes A,B] [--reserve 1=75]
  ci-shard-tests.py table   --kind backend --shards 4 [--lanes 2]
  ci-shard-tests.py refresh TRX_DIR...        merge measured class durations into tests/ci-durations.json
  ci-shard-tests.py --self-test

`plan` prints one dotnet test --filter expression per lane (an empty line for an empty lane).
"""
from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
import xml.etree.ElementTree as ET
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DURATIONS = ROOT / "tests" / "ci-durations.json"
KINDS = {
    "backend": ("tests/Jularr.Backend.Tests", "Jularr.Backend.Tests"),
    "e2e": ("tests/Jularr.E2E.Tests", "Jularr.E2E.Tests"),
}
DEFAULT_WEIGHT = 10.0

NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)
TEST_CLASS = re.compile(r"\[TestClass(?:\([^\]]*\))?\][\s\S]{0,300}?\bclass\s+(\w+)")


def discover(kind: str, root: Path = ROOT) -> list[str]:
    """Fully qualified names of the [TestClass] classes of a test project (from source, no build)."""
    folder = root / KINDS[kind][0]
    found: set[str] = set()
    for file in sorted(folder.rglob("*.cs")):
        if {"bin", "obj"} & set(file.relative_to(folder).parts):
            continue
        text = file.read_text(encoding="utf-8-sig", errors="replace")
        namespace = NAMESPACE.search(text)
        for match in TEST_CLASS.finditer(text):
            found.add(f"{namespace.group(1) if namespace else KINDS[kind][1]}.{match.group(1)}")
    return sorted(found)


def load_durations(kind: str, path: Path = DURATIONS) -> dict[str, float]:
    if not path.exists():
        return {}
    return {name: float(seconds) for name, seconds in json.loads(path.read_text(encoding="utf-8")).get(kind, {}).items()}


def weights(classes: list[str], durations: dict[str, float]) -> dict[str, float]:
    known = [durations[name] for name in classes if name in durations]
    unknown = statistics.median(known) if known else DEFAULT_WEIGHT
    return {name: durations.get(name, unknown) for name in classes}


def pack(items: dict[str, float], bins: int, reserve: dict[int, float] | None = None) -> list[list[str]]:
    """Greedy longest-first packing; deterministic (weight desc, name asc; lightest bin, lowest index)."""
    loads = [float((reserve or {}).get(index, 0)) for index in range(bins)]
    result: list[list[str]] = [[] for _ in range(bins)]
    for name, weight in sorted(items.items(), key=lambda item: (-item[1], item[0])):
        index = min(range(bins), key=lambda candidate: (loads[candidate], candidate))
        result[index].append(name)
        loads[index] += weight
    return [sorted(names) for names in result]


def shards_of(kind: str, shards: int, lanes: int = 1, only: list[str] | None = None,
              reserve: dict[int, float] | None = None, root: Path = ROOT,
              durations: dict[str, float] | None = None) -> list[list[list[str]]]:
    """shards -> lanes -> class names. `only` restricts to named classes (short or fully qualified)."""
    classes = discover(kind, root)
    if only is not None:
        wanted = set()
        for name in only:
            matches = [full for full in classes if full == name or full.endswith("." + name)]
            if not matches:
                print(f"::warning::Test class {name} is not in {KINDS[kind][0]}; skipped.", file=sys.stderr)
            wanted.update(matches)
        classes = sorted(wanted)
    measured = load_durations(kind) if durations is None else durations
    size = weights(classes, measured)
    plan = []
    for shard_classes in pack(size, shards, reserve):
        plan.append(pack({name: size[name] for name in shard_classes}, lanes))
    return plan


def to_filter(classes: list[str]) -> str:
    return "|".join(f"FullyQualifiedName~{name}." for name in classes)


def parse_shard(text: str) -> tuple[int, int]:
    index, _, count = text.partition("/")
    shard, shards = int(index), int(count)
    if not 1 <= shard <= shards:
        raise SystemExit(f"Shard {text} is out of range (use 1/{shards} to {shards}/{shards}).")
    return shard, shards


def parse_reserve(items: list[str]) -> dict[int, float]:
    return {int(key) - 1: float(value) for key, _, value in (item.partition("=") for item in items)}


def seconds(value: str) -> float:
    hours, minutes, rest = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(rest)


def trx_class_durations(folder: Path) -> dict[str, dict[str, float]]:
    """kind -> class -> summed seconds, from every *.trx below folder."""
    result: dict[str, dict[str, float]] = {kind: {} for kind in KINDS}
    for file in sorted(folder.rglob("*.trx")):
        root = ET.parse(file).getroot()
        classes = {}
        for unit in root.findall(".//{*}UnitTest"):
            method = unit.find("{*}TestMethod")
            if method is not None:
                classes[unit.attrib["id"]] = method.attrib["className"].split(",")[0].strip()
        for outcome in root.findall(".//{*}UnitTestResult"):
            name = classes.get(outcome.attrib.get("testId", ""))
            if not name:
                continue
            for kind, (_, namespace) in KINDS.items():
                if name.startswith(namespace + "."):
                    result[kind][name] = result[kind].get(name, 0.0) + seconds(outcome.attrib["duration"])
    return result


def refresh(folders: list[Path], path: Path = DURATIONS) -> int:
    document = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    changed = 0
    for folder in folders:
        for kind, measured in trx_class_durations(folder).items():
            stored = document.setdefault(kind, {})
            for name, value in measured.items():
                stored[name] = round(value, 1)
                changed += 1
    for kind in KINDS:
        live = set(discover(kind))
        document[kind] = {name: document[kind][name] for name in sorted(document.get(kind, {})) if name in live}
    document["updated"] = date.today().isoformat()
    path.write_text(json.dumps(document, indent=1, sort_keys=False) + "\n", encoding="utf-8")
    print(f"Updated {changed} class durations in {path.relative_to(ROOT)}.")
    return 0


def table(kind: str, shards: int, lanes: int, reserve: dict[int, float] | None = None) -> str:
    classes = discover(kind)
    measured = load_durations(kind)
    size = weights(classes, measured)
    unknown = [name for name in classes if name not in measured]
    rows = ["| Shard | Classes | Expected test time | Lane times |", "| ---: | ---: | ---: | --- |"]
    for index, lane_plan in enumerate(shards_of(kind, shards, lanes, reserve=reserve), start=1):
        lane_totals = [sum(size[name] for name in lane) for lane in lane_plan]
        rows.append(f"| {index}/{shards} | {sum(len(lane) for lane in lane_plan)} | {max(lane_totals):.0f} s | "
                    + " / ".join(f"{total:.0f} s" for total in lane_totals) + " |")
    total = sum(size.values())
    rows.append(f"\n{len(classes)} classes, {total:.0f} s measured in total, {len(unknown)} without a measurement "
                f"(weighted as the median class, {statistics.median([size[c] for c in classes if c in measured] or [DEFAULT_WEIGHT]):.1f} s).")
    return "\n".join(rows)


def self_test() -> None:
    items = {f"C{i}": float(w) for i, w in enumerate([240, 150, 100, 50, 49, 45, 40, 38, 35, 30, 20, 10, 5, 5, 1])}
    for bins in (1, 2, 4, 7):
        packed = pack(items, bins)
        flat = [name for shard in packed for name in shard]
        assert sorted(flat) == sorted(items) and len(flat) == len(set(flat)), "every unit exactly once"
        loads = [sum(items[name] for name in shard) for shard in packed]
        assert max(loads) <= max(max(items.values()), sum(items.values()) / bins * 4 / 3 + 1), loads
    assert pack(items, 4) == pack(dict(reversed(list(items.items()))), 4), "independent of input order"
    assert pack({"A": 1, "B": 1}, 2, {0: 50}) == [[], ["A", "B"]], "reserved time is honoured"
    # a class without measurement weighs as the median measured class
    size = weights(["A", "B", "C", "New"], {"A": 10, "B": 30, "C": 20})
    assert size["New"] == 20
    assert weights(["Only"], {})["Only"] == DEFAULT_WEIGHT
    assert parse_shard("3/4") == (3, 4) and parse_reserve(["1=75"]) == {0: 75.0}
    assert to_filter(["N.A", "N.B"]) == "FullyQualifiedName~N.A.|FullyQualifiedName~N.B."
    assert seconds("00:01:02.5") == 62.5

    # discovery against a temporary tree: block and file-scoped namespaces, attributes, non-test classes
    import tempfile
    with tempfile.TemporaryDirectory() as folder:
        project = Path(folder) / KINDS["backend"][0]
        (project / "obj").mkdir(parents=True)
        (project / "A.cs").write_text("namespace X.Y\n{\n    [TestClass]\n    public sealed class AlphaTests { }\n    public class Helper { }\n}\n")
        (project / "B.cs").write_text("namespace X.Z;\n[TestClass]\n[DoNotParallelize]\npublic static class BetaTests { }\n")
        (project / "obj" / "C.cs").write_text("namespace Gen;\n[TestClass]\npublic class Ignored { }\n")
        assert discover("backend", Path(folder)) == ["X.Y.AlphaTests", "X.Z.BetaTests"]
        plan = shards_of("backend", 2, 1, root=Path(folder), durations={"X.Y.AlphaTests": 9.0})
        assert sorted(name for shard in plan for lane in shard for name in lane) == ["X.Y.AlphaTests", "X.Z.BetaTests"]
        only = shards_of("backend", 2, 1, only=["BetaTests"], root=Path(folder), durations={})
        assert [name for shard in only for lane in shard for name in lane] == ["X.Z.BetaTests"]

    # the real test projects: every class lands in exactly one shard and lane for any shard count
    for kind in KINDS:
        classes = discover(kind)
        assert len(classes) > 20, f"found only {len(classes)} {kind} test classes"
        for shards, lanes in ((1, 1), (3, 1), (4, 2), (5, 2)):
            plan = shards_of(kind, shards, lanes)
            flat = [name for shard in plan for lane in shard for name in lane]
            assert sorted(flat) == classes and len(flat) == len(set(flat)), (kind, shards, lanes)
    stored = json.loads(DURATIONS.read_text(encoding="utf-8"))
    assert all(isinstance(v, (int, float)) and v >= 0 for kind in KINDS for v in stored[kind].values())

    # every shard matrix in the workflows is complete: 1/N ... N/N
    seen = 0
    for workflow in sorted((ROOT / ".github" / "workflows").glob("*.yml")):
        entries: dict[str, list[tuple[int, int]]] = {}
        for kind, index, count in re.findall(r"\{\s*kind:\s*(\w+),\s*shard:\s*(\d+)/(\d+)[^}]*\}",
                                             workflow.read_text(encoding="utf-8")):
            entries.setdefault(kind, []).append((int(index), int(count)))
        for kind, listed in entries.items():
            count = listed[0][1]
            assert listed == [(index, count) for index in range(1, count + 1)], (workflow.name, kind, listed)
            seen += 1
    assert seen >= 3, "the workflows no longer list their shards in the checked form"


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        print("Test shard self-checks passed.")
        return 0
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    plan = commands.add_parser("plan")
    plan.add_argument("--kind", choices=KINDS, required=True)
    plan.add_argument("--shard", required=True, help="i/N")
    plan.add_argument("--lanes", type=int, default=1)
    plan.add_argument("--classes", help="comma separated class names; restricts the plan to them")
    plan.add_argument("--reserve", action="append", default=[], help="SHARD=SECONDS already spent on that shard")
    show = commands.add_parser("table")
    show.add_argument("--kind", choices=KINDS, required=True)
    show.add_argument("--shards", type=int, required=True)
    show.add_argument("--lanes", type=int, default=1)
    show.add_argument("--reserve", action="append", default=[], help="SHARD=SECONDS already spent on that shard")
    merge = commands.add_parser("refresh")
    merge.add_argument("folders", nargs="+", type=Path)
    args = parser.parse_args()

    if args.command == "plan":
        shard, shards = parse_shard(args.shard)
        only = [name for name in args.classes.split(",") if name] if args.classes is not None else None
        lanes = shards_of(args.kind, shards, args.lanes, only, parse_reserve(args.reserve))[shard - 1]
        for lane in lanes:
            print(to_filter(lane))
        total = sum(len(lane) for lane in lanes)
        print(f"Shard {shard}/{shards}: {total} {args.kind} test classes in {len(lanes)} lane(s).", file=sys.stderr)
    elif args.command == "table":
        print(table(args.kind, args.shards, args.lanes, parse_reserve(args.reserve)))
    else:
        return refresh(args.folders)
    return 0


if __name__ == "__main__":
    sys.exit(main())
