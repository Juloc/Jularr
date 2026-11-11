#!/usr/bin/env python3
"""Decide whether a failed Backend / Backend Full run is re-run once (.github/workflows/ci-retry.yml).

Only infrastructure failures are re-run, only attempt 1, never a failed test or build step:

  all     GitHub created no job at all, or every job passed and the run is still red (a job such as
          `gate` was never created): the whole run starts again.
  failed  Every failed job broke in an allowlisted setup step (runner set-up, service containers,
          checkout, toolchains, caches, the shared build, tool and browser downloads, artifact uploads)
          or outside its steps (runner lost, job never started): the failed jobs and their dependents
          start again.
  none    Everything else: a test, compile or check step failed or was cut off (also in a cancelled job:
          a timed-out job can look cancelled), only cancelled jobs remain, it is attempt 2 or later, a newer
          run of the branch exists (re-running the old one would cancel it through `concurrency`), or the
          run is not a watched workflow. A cancelled job is never the reason for a re-run.

  ci-classify-failure.py decide --repo OWNER/NAME --run-id ID   read the run with `gh api` (GH_TOKEN),
                                                                 write decision/reason to GITHUB_OUTPUT
                                                                 and a table to GITHUB_STEP_SUMMARY
  ci-classify-failure.py --self-test

The script only reads; the workflow starts the re-run.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from typing import Callable
from urllib.parse import quote

WATCHED = {".github/workflows/backend.yml", ".github/workflows/backend-pr.yml"}

# Steps that only prepare a job. A failure there is the network, the runner or GitHub, not the code.
SETUP_STEPS = {
    "Set up job",
    "Initialize containers",
    "Stop containers",
    "Complete job",
    "Prepare .NET runtime mutex directory",
    "Fetch the base and head commits",
    # Never fails on its own (it falls back to tests/ci-durations.json); a crash there is the runner.
    "Fetch the measured test durations of recent main runs",
    "Cache the Playwright browser",
    "Install Chromium",
    "Keep smoke test results",
    "Keep test results",
    "Keep timing report",
    "Keep the evidence of a failed browser run",
    "Keep the hang and crash evidence",
}
# Unnamed `uses:` steps of the official actions (checkout, setup-*, cache, artifacts) and of the
# repository's own set-up actions (restore-build, install-tools, install-chromium, pack-build), and
# the post steps of any action (cache save, clean-up).
SETUP_PREFIXES = ("Run actions/", "Run ./.github/actions/", "Post ")
# The summary jobs (`gate`, `test`) fail in this step only because a job they need failed.
CONSEQUENCE_PREFIX = "Require "
RUNNER_LOST = (
    "lost communication with the server",
    "received a shutdown signal",
    "was not acquired by runner",
    "the hosted runner encountered an error",
)
FAILED = {"failure", "timed_out", "startup_failure"}
PASSED = {"success", "skipped", "neutral"}

ACTIONS = {
    "all": "re-run the whole run",
    "failed": "re-run the failed jobs",
    "none": "no re-run",
}


def is_setup(step: str) -> bool:
    return step in SETUP_STEPS or step.startswith(SETUP_PREFIXES)


def first_broken_step(job: dict) -> dict | None:
    """The first step that did not pass: a failed one, else a cancelled or unfinished one."""
    steps = sorted(job.get("steps") or [], key=lambda step: step.get("number", 0))
    broken = [step for step in steps if step.get("conclusion") not in PASSED]
    failed = [step for step in broken if step.get("conclusion") == "failure"]
    return (failed or broken or [None])[0]


def job_cause(job: dict, annotations: Callable[[dict], list[str]]) -> tuple[str, str]:
    """('infra' | 'consequence' | 'code', explanation) for one failed job."""
    step = first_broken_step(job)
    if step is None:
        return "infra", "failed outside its steps (runner lost or job not started)"
    name = step.get("name", "")
    if step.get("conclusion") == "failure":
        if name.startswith(CONSEQUENCE_PREFIX):
            return "consequence", f"`{name}` reports a failed job it needs"
        if is_setup(name):
            return "infra", f"set-up step `{name}` failed"
        return "code", f"step `{name}` failed"
    # Cancelled or unfinished: a lost runner, or the job ran into its timeout.
    messages = " ".join(annotations(job)).lower()
    if any(text in messages for text in RUNNER_LOST):
        return "infra", f"runner lost during `{name}`"
    if is_setup(name):
        return "infra", f"set-up step `{name}` did not finish"
    return "code", f"step `{name}` did not finish (timeout or cancel)"


def decide(decision: str, reason: str, rows: list[list[str]] | None = None) -> dict:
    return {"decision": decision, "reason": reason, "rows": rows or []}


def classify(run: dict, jobs: list[dict], newer_runs: list[dict],
             annotations: Callable[[dict], list[str]]) -> dict:
    """Decision for one run: {'decision', 'reason', 'rows'}; rows describe the failed jobs."""
    if run.get("path") not in WATCHED:
        return decide("none", f"{run.get('path')} is not a watched workflow (Backend, Backend Full)")
    if run.get("status") != "completed" or run.get("conclusion") != "failure":
        return decide("none", f"the run is {run.get('status')}/{run.get('conclusion')}, not a completed failure")
    if run.get("run_attempt") != 1:
        return decide("none", f"attempt {run.get('run_attempt')}: only attempt 1 is ever re-run")
    result = classify_jobs(jobs, annotations)
    newer = [other for other in newer_runs if other.get("id") != run.get("id")
             and other.get("created_at", "") > run.get("created_at", "")]
    if result["decision"] != "none" and newer:
        return decide("none", f"{result['reason']}, but run {newer[0].get('id')} of the same branch is newer: "
                              "re-running this one would cancel it", result["rows"])
    return result


def classify_jobs(jobs: list[dict], annotations: Callable[[dict], list[str]]) -> dict:
    """The decision the jobs of a failed attempt 1 alone call for."""
    if not jobs:
        return decide("all", "GitHub created no job for this run")
    rows = []
    causes = set()
    for job in sorted(jobs, key=lambda job: job.get("name", "")):
        conclusion = job.get("conclusion")
        if conclusion not in FAILED and conclusion != "cancelled":
            continue
        cause, explanation = job_cause(job, annotations)
        # `rerun --failed` also starts cancelled jobs: a cut-off test step in one blocks the re-run, but a
        # cancelled job is never the reason for one.
        if conclusion == "cancelled" and cause != "code":
            cause = "cancelled"
        causes.add(cause)
        rows.append([job.get("name", ""), conclusion or "", cause, explanation])

    if not rows:
        if all(job.get("conclusion") in PASSED for job in jobs):
            return decide("all", "the run is red although every job it created passed (a job such as gate was never created)")
        return decide("none", "no job failed or was cancelled, but not every job passed")
    if "code" in causes:
        return decide("none", "a test, compile or check step failed or was cut off", rows)
    if "infra" not in causes:
        return decide("none", "no failed job points at the infrastructure (only the summary job failed or jobs were cancelled)", rows)
    return decide("failed", "every failed job broke in set-up or lost its runner", rows)


def summary(run: dict, result: dict) -> str:
    lines = [
        f"### CI retry: {run.get('name', 'run')} {run.get('id')} (attempt {run.get('run_attempt')})",
        "",
        f"Decision: **{ACTIONS[result['decision']]}** (`{result['decision']}`)",
        "",
        f"Reason: {result['reason']}",
        "",
    ]
    if run.get("html_url"):
        lines += [f"Run: {run['html_url']}", ""]
    if result["rows"]:
        lines += ["| Failed job | Conclusion | Cause | Detail |", "| --- | --- | --- | --- |"]
        lines += ["| " + " | ".join(cell.replace("|", "\\|") for cell in row) + " |" for row in result["rows"]]
        lines.append("")
    return "\n".join(lines) + "\n"


def gh(*args: str) -> str:
    return subprocess.run(["gh", "api", *args], check=True, capture_output=True, text=True).stdout


def fetch(repo: str, run_id: str) -> tuple[dict, list[dict], list[dict], Callable[[dict], list[str]]]:
    run = json.loads(gh(f"repos/{repo}/actions/runs/{run_id}"))
    attempt = run.get("run_attempt", 1)
    jobs = [json.loads(line) for line in gh(
        "--paginate", "--jq", ".jobs[]", f"repos/{repo}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100"
    ).splitlines() if line.strip()]
    newer: list[dict] = []
    # Only push and PR runs share a cancelling concurrency group; a manual Backend Full run has its own.
    if run.get("event") in ("push", "pull_request") and run.get("head_branch"):
        query = f"branch={quote(run['head_branch'], safe='')}&event={run['event']}&per_page=20"
        newer = json.loads(gh(f"repos/{repo}/actions/workflows/{run['workflow_id']}/runs?{query}")).get("workflow_runs", [])

    def annotations(job: dict) -> list[str]:
        return [item.get("message", "") for item in json.loads(gh(f"repos/{repo}/check-runs/{job['id']}/annotations?per_page=100"))]

    return run, jobs, newer, annotations


def decide_command(repo: str, run_id: str) -> int:
    try:
        run, jobs, newer, annotations = fetch(repo, run_id)
        result = classify(run, jobs, newer, annotations)
    except subprocess.CalledProcessError as error:
        print(f"::error::Reading run {run_id} failed: {(error.stderr or '').strip() or error}")
        return 1
    report = summary(run, result)
    print(report)
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf8") as output:
            output.write(report)
    if path := os.environ.get("GITHUB_OUTPUT"):
        with open(path, "a", encoding="utf8") as output:
            output.write(f"decision={result['decision']}\nreason={result['reason']}\n")
    return 0


# Self-test fixtures: shapes of real runs (job name, conclusion, steps as (name, conclusion)).
def job(name: str, conclusion: str, *steps: tuple[str, str], job_id: int = 0) -> dict:
    return {"id": job_id, "name": name, "conclusion": conclusion,
            "steps": [{"number": index + 1, "name": step, "conclusion": result} for index, (step, result) in enumerate(steps)]}


SETUP = [("Set up job", "success"), ("Initialize containers", "success"), ("Run actions/checkout@v7", "success"),
         ("Run ./.github/actions/restore-build", "success")]


def shard(name: str, conclusion: str = "success", test: str = "success", kind: str = "backend") -> dict:
    step = "Run the backend shard" if kind == "backend" else "Run the browser shard"
    return job(name, conclusion, *SETUP, ("Run ./.github/actions/install-tools", "success"), (step, test),
               ("Keep timing report", "success"), ("Complete job", "success"))


def full_run(**overrides) -> dict:
    run = {"id": 100, "name": "Backend Full", "path": ".github/workflows/backend.yml", "status": "completed",
           "conclusion": "failure", "run_attempt": 1, "event": "workflow_dispatch", "created_at": "2026-11-09T17:57:45Z"}
    run.update(overrides)
    return run


GATE_FAILED = job("gate", "failure", ("Set up job", "success"), ("Require the build, smoke and all shards", "failure"))
BUILD = job("build", "success", ("Set up job", "success"), ("Run actions/checkout@v7", "success"),
            ("Fetch the measured test durations of recent main runs", "success"),
            ("Compile the backend and browser test projects once", "success"), ("Plan the test shards", "success"),
            ("Run ./.github/actions/pack-build", "success"))
SMOKE = job("smoke", "success", ("Set up job", "success"), ("Run ./.github/actions/restore-build", "success"),
            ("Critical backend tests (startup, schema, migrations, sessions, setup gate)", "success"))


def self_test() -> None:
    no_annotations = lambda job: []  # noqa: E731
    lost = lambda job: ["The hosted runner: GitHub Actions 1000042120 lost communication with the server."]  # noqa: E731
    timeout = lambda job: ["The job running on runner GitHub Actions 7 has exceeded the maximum execution time of 18 minutes."]  # noqa: E731

    def check(name: str, expected: str, run: dict, jobs: list[dict], newer: list[dict] | None = None,
              annotations: Callable[[dict], list[str]] = no_annotations) -> None:
        result = classify(run, jobs, newer or [], annotations)
        assert result["decision"] == expected, f"{name}: expected {expected}, got {result}"
        assert result["reason"], name
        assert ACTIONS[result["decision"]] in summary(run, result), name

    every_shard = [shard(f"backend ({i}/6)") for i in range(1, 7)] + [shard(f"e2e ({i}/6)", kind="e2e") for i in range(1, 7)]
    # 42669663513 / 42669292640: every shard green, gate never created, run red.
    check("42669663513", "all", full_run(id=42669663513), [BUILD, SMOKE, *every_shard])
    # build, smoke and shards: smoke runs beside the shards. A failed smoke test or build step is no re-run;
    # a smoke job that could not unpack the build is.
    check("smoke test beside green shards", "none", full_run(),
          [BUILD, job("smoke", "failure", ("Run ./.github/actions/restore-build", "success"),
                      ("Critical backend tests (startup, schema, migrations, sessions, setup gate)", "failure")),
           *every_shard, GATE_FAILED])
    check("smoke restore-build", "failed", full_run(),
          [BUILD, job("smoke", "failure", ("Set up job", "success"), ("Run ./.github/actions/restore-build", "failure")),
           *every_shard, GATE_FAILED])
    check("build compile", "none", full_run(),
          [job("build", "failure", ("Run actions/checkout@v7", "success"), ("Compile the backend and browser test projects once", "failure")),
           job("smoke", "skipped"), GATE_FAILED])
    check("build plan", "none", full_run(),
          [job("build", "failure", ("Fetch the measured test durations of recent main runs", "success"), ("Plan the test shards", "failure")),
           GATE_FAILED])
    check("build fetch crashed", "failed", full_run(),
          [job("build", "failure", ("Run actions/checkout@v7", "success"), ("Fetch the measured test durations of recent main runs", "failure")),
           GATE_FAILED])
    check("prepare base and head fetch", "failed", full_run(name="Backend", path=".github/workflows/backend-pr.yml"),
          [job("prepare", "failure", ("Run actions/checkout@v7", "success"), ("Fetch the base and head commits", "failure")),
           job("test", "failure", ("Require prepare and the shards", "failure"))])
    # 42672877493 / 42671517066: no job at all.
    check("42672877493", "all", full_run(id=42672877493), [])
    # 42670336356 (Backend PR): prepare and both shards green, full-regression skipped, `test` missing.
    check("42670336356", "all", full_run(id=42670336356, name="Backend", path=".github/workflows/backend-pr.yml", event="pull_request",
                                         head_branch="fix/x"),
          [job("prepare", "success", ("Set up job", "success")), job("full-regression", "skipped"),
           shard("backend (1/2)"), shard("backend (2/2)")])
    # 42525493314: a browser test failed; gate failed as a consequence; other shards cancelled by fail-fast.
    check("42525493314", "none", full_run(id=42525493314, event="push", head_branch="main"),
          [SMOKE, shard("e2e (3/4)", "failure", "failure", "e2e"), shard("e2e (4/4)", "cancelled", "cancelled", "e2e"),
           shard("backend (1/4)", "cancelled", "cancelled"), shard("backend (3/4)"), GATE_FAILED])
    # 41595550025 / 40020197549: install-tools failed (tool download); gate followed. Without fail-fast the
    # other shards run to their end.
    install_failed = job("backend (4/4)", "failure", *SETUP, ("Run ./.github/actions/install-tools", "failure"),
                         ("Run the backend shard", "skipped"), ("Keep timing report", "success"))
    check("install-tools", "failed", full_run(id=41595550025),
          [SMOKE, install_failed, shard("backend (1/4)"), shard("e2e (1/4)", kind="e2e"), GATE_FAILED])
    # A cancelled job would be re-run with the failed ones: its cut-off test step blocks (a job timeout can
    # look cancelled), a job cancelled in set-up does not, and cancelled jobs alone never re-run.
    check("infra next to a cut-off test", "none", full_run(),
          [SMOKE, install_failed, shard("backend (1/4)", "cancelled", "cancelled"), GATE_FAILED])
    check("infra next to a set-up cancel", "failed", full_run(),
          [SMOKE, install_failed, job("e2e (1/4)", "cancelled", *SETUP, ("Install Chromium", "cancelled")), GATE_FAILED])
    check("only set-up cancels", "none", full_run(),
          [SMOKE, job("e2e (1/4)", "cancelled", *SETUP, ("Install Chromium", "cancelled")), GATE_FAILED])
    check("cancelled job, runner lost", "none", full_run(), [SMOKE, shard("backend (1/4)", "cancelled", "cancelled"), GATE_FAILED],
          annotations=lambda job: ["The hosted runner: GitHub Actions 3 lost communication with the server."])
    # 40227922339: the shared build could not be downloaded; 40226250729: setup-dotnet failed in prepare.
    check("restore-build", "failed", full_run(name="Backend", path=".github/workflows/backend-pr.yml"),
          [job("prepare", "success"), job("backend (2/2)", "failure", ("Set up job", "success"), ("Run actions/checkout@v7", "success"),
                                          ("Run ./.github/actions/restore-build", "failure")),
           job("test", "failure", ("Require prepare and the shards", "failure"))])
    check("setup-dotnet", "failed", full_run(name="Backend", path=".github/workflows/backend-pr.yml"),
          [job("prepare", "failure", ("Set up job", "success"), ("Detect affected project files", "success"),
               ("Run actions/setup-dotnet@v6", "failure")),
           job("test", "failure", ("Require prepare and the shards", "failure"))])
    # An opted-in Full PR names the reusable jobs `full-regression / ...`.
    check("reusable install-tools", "failed", full_run(name="Backend", path=".github/workflows/backend-pr.yml"),
          [job("full-regression / backend (4/4)", "failure", *SETUP, ("Run ./.github/actions/install-tools", "failure")),
           job("full-regression / gate", "failure", ("Require smoke and all shards", "failure")),
           job("test", "failure", ("Require prepare and the shards", "failure"))])
    # Never a test, compile or smoke failure, also not when a set-up failure happened next to it.
    check("infra next to a test failure", "none", full_run(), [install_failed, shard("backend (2/4)", "failure", "failure"), GATE_FAILED])
    check("smoke tests", "none", full_run(),
          [job("smoke", "failure", ("Set up job", "success"), ("Critical backend tests (startup, schema, migrations, sessions, setup gate)", "failure")),
           GATE_FAILED])
    check("compile", "none", full_run(name="Backend", path=".github/workflows/backend-pr.yml"),
          [job("prepare", "failure", ("Run actions/setup-dotnet@v6", "success"), ("Compile the backend test project and its dependencies", "failure")),
           job("test", "failure", ("Require prepare and the shards", "failure"))])
    # A lost runner is infrastructure; a test step cut off by the job timeout is not.
    cut_off = job("backend (2/4)", "failure", *SETUP, ("Run the backend shard", "cancelled"), job_id=7)
    check("runner lost", "failed", full_run(), [SMOKE, cut_off, GATE_FAILED], annotations=lost)
    check("job timeout in a test", "none", full_run(), [SMOKE, cut_off, GATE_FAILED], annotations=timeout)
    check("job timeout in set-up", "failed", full_run(),
          [job("e2e (1/4)", "failure", *SETUP, ("Install Chromium", "cancelled")), GATE_FAILED], annotations=timeout)
    check("failed without a step", "failed", full_run(), [SMOKE, job("e2e (2/4)", "failure"), GATE_FAILED])
    check("only the summary failed", "none", full_run(), [SMOKE, *every_shard, GATE_FAILED])
    check("cancelled, nothing failed", "none", full_run(), [SMOKE, shard("backend (1/4)", "cancelled", "cancelled")])
    # Guards: attempt 2+, not a failure, not watched, a newer run of the same branch.
    check("attempt 2", "none", full_run(run_attempt=2), [])
    check("attempt 2 with set-up failure", "none", full_run(run_attempt=2), [install_failed, GATE_FAILED])
    check("cancelled run", "none", full_run(conclusion="cancelled"), [])
    check("success", "none", full_run(conclusion="success"), [SMOKE])
    check("in progress", "none", full_run(status="in_progress", conclusion=None), [])
    check("other workflow", "none", full_run(name="Frontend", path=".github/workflows/frontend.yml"), [])
    check("newer push", "none", full_run(event="push", head_branch="main"), [],
          newer=[{"id": 100, "created_at": "2026-11-09T17:57:45Z"}, {"id": 101, "created_at": "2026-11-09T18:05:00Z"}])
    check("only older runs", "all", full_run(event="push", head_branch="main"), [],
          newer=[{"id": 99, "created_at": "2026-11-09T17:00:00Z"}, {"id": 100, "created_at": "2026-11-09T17:57:45Z"}])

    # Every set-up step name used by the watched workflows is still recognised.
    for name in ("Set up job", "Run actions/checkout@v7", "Run actions/setup-node@v7", "Run actions/cache@v6",
                 "Run ./.github/actions/install-tools", "Run ./.github/actions/restore-build", "Run ./.github/actions/pack-build",
                 "Post Run actions/cache@v6", "Install Chromium", "Keep the hang and crash evidence",
                 "Fetch the base and head commits", "Fetch the measured test durations of recent main runs"):
        assert is_setup(name), name
    for name in ("Run the backend shard", "Run the browser shard", "Run the selected test classes of this shard",
                 "Real-tool tests (FFmpeg HLS, JPEG XL covers)", "Frontend unit tests", "Compile the backend and browser test projects once",
                 "Verify the CI scripts (test shards and selection)", "Detect affected project files", "Plan the test shards",
                 "Select the tests for the changed areas and run the smoke methods"):
        assert not is_setup(name), name


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        print("CI failure classifier self-checks passed.")
        return 0
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    decide_parser = commands.add_parser("decide")
    decide_parser.add_argument("--repo", required=True)
    decide_parser.add_argument("--run-id", required=True)
    args = parser.parse_args()
    if not args.run_id.isdigit():
        parser.error("--run-id must be a number")
    return decide_command(args.repo, args.run_id)


if __name__ == "__main__":
    sys.exit(main())
