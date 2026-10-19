#!/usr/bin/env python3
"""Print per-test TRX durations without modifying test execution or outcomes."""
from pathlib import Path
import os
import re
import sys
import xml.etree.ElementTree as ET


def seconds(value: str) -> float:
    match = re.fullmatch(r"(\\d+):(\\d+):(\\d+)(?:\\.(\\d+))?", value)
    if not match:
        raise ValueError(f"Unrecognized TRX duration: {value}")
    hours, minutes, whole, fraction = match.groups()
    return int(hours) * 3600 + int(minutes) * 60 + int(whole) + float(f"0.{fraction or '0'}")


def main() -> int:
    folder = Path(sys.argv[1])
    files = sorted(folder.glob("*.trx"))
    if not files:
        print(f"No TRX results in {folder} (test step may have failed before discovery).")
        return 0

    tests = []
    for file in files:
        root = ET.parse(file).getroot()
        for result in root.findall(".//{*}UnitTestResult"):
            tests.append(
                (
                    seconds(result.attrib["duration"]),
                    result.attrib.get("testName", "(unnamed)"),
                    result.attrib.get("outcome", "Unknown"),
                    file.name,
                )
            )

    tests.sort(reverse=True)
    failed = sum(outcome != "Passed" for _, _, outcome, _ in tests)
    total = sum(duration for duration, _, _, _ in tests)
    rows = [
        f"### Test durations: {folder.name}",
        "",
        f"{len(tests)} cases, {failed} not passed, {total:.1f} s cumulative test duration.",
        "Cumulative duration is not wall-clock time.",
        "",
        "| Duration | Outcome | Test | Report |",
        "| ---: | --- | --- | --- |",
    ]
    for duration, name, outcome, file in tests[:20]:
        safe_name = name.replace("|", "\\|").replace("\\n", " ")
        rows.append(f"| {duration:.2f} s | {outcome} | {safe_name} | {file} |")

    report = "\\n".join(rows) + "\\n"
    print(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf8") as output:
            output.write(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
