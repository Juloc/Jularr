#!/usr/bin/env python3
"""Choose a bounded, change-aware backend regression set for pull requests.

All backend test code is compiled separately. This selector adds focused test
classes to a small invariant smoke suite. Full CI still owns exhaustive runs.
"""
from __future__ import annotations

from pathlib import PurePosixPath
import json
import subprocess
import sys

NAMESPACE = "Jularr.Backend.Tests."
SMOKE = (
    "ServiceApiTests.UnknownErrors_ReadAsUnknownAndShowTheirMessageToAdministratorsOnly",
    "ServiceApiTests.Registration_MissingDatabaseConfiguration_IsRejected",
    "ServiceApiTests.Registration_BackendOwnsDatabase_DirectAndHttpServicesShareRuntime",
    "AuthenticationTests.Session_ExpiredOrRevoked_IsNotRenewed",
    "InstanceModuleDependencyTests.Modules_Read_NameWhatEachNeeds",
)

# The `smoke` job of Backend Full: startup, schema, migrations, sessions and the first-owner
# setup gate. Whole classes plus SMOKE; the Backend Full shards leave these out (--boot-units).
BOOT_CLASSES = ("DatabaseBaselineTests", "DatabaseMigrationTests", "ServiceFoundationTests",
                "SessionLockTests", "AccountSchemaTests")
BOOT_METHODS = ("SetupTests.Razor_FirstOwner_ValidatesRollsBackAndSignsIn",
                "SetupTests.FirstOwner_ConcurrentCreation_OnlyOneSucceedsAndBootstrapStaysClosed")

# A path may belong to several areas (e.g. downloads + storage). The mappings
# are deliberately plain to make changes auditable in code review. Keep them
# aligned with tests/Jularr.Backend.Tests.
AREAS = (
    (("acquisition", "download", "wanted", "newznab", "sabnzbd", "sonarr",
      "radarr", "lidarr", "readarr", "usenet", "torrent", "arrplus"),
     ("AcquisitionTests", "UsenetTests", "ManualImportLogicTests")),
    (("storage", "mount", "smb", "filesystem", "fileimport"),
     ("StorageTests",)),
    (("authentication", "session", "account", "permission", "device", "profile", "servicegate"),
     ("AuthenticationTests", "DeviceTests")),
    (("library", "media", "catalog", "metadata", "provider", "discover", "tmdb", "anilist"),
     ("LibraryTests", "DiscoverTests")),
    (("playback", "stream", "transcod", "subtitle", "player"),
     ("PlaybackTests",)),
    (("reading", "book", "epub", "manga", "comic", "novel"),
     ("ReadingTests", "BookTests")),
    (("music", "musicbrainz", "audio"),
     ("MusicTests",)),
    (("speech", "tts", "piper", "sherpa"),
     ("SpeechTests", "SpeechDiscoveryTests")),
    (("backup",), ("BackupTests",)),
    (("sql", "migration"), ("SqlContextTests",)),
    (("module", "instance"),
     ("InstanceModuleDependencyTests",)),
)

# Safety-sensitive flows should be selected even when the PR changes a
# shared infrastructure class rather than a precisely named feature path.
SHARED = ("ServiceApiTests", "SqlContextTests")
CORE_PREFIXES = (
    "src/Jularr.Backend/",
    "src/Jularr.Integrations/",
    "src/Jularr.Workers/",
)
# A change to what plans and runs the PR shards sends every area class through them: large classes
# are split by method and the plan spreads over several shards, so the PR itself proves that path.
CI_MACHINERY = (
    "scripts/ci-shard-tests.py",
    "scripts/ci-select-backend-tests.py",
    "tests/ci-durations.json",
    ".github/workflows/backend-pr.yml",
    ".github/actions/restore-build/",
    ".github/actions/install-tools/",
    ".github/actions/pack-build/",
)


def choose(paths: list[str]) -> tuple[str, tuple[str, ...]]:
    methods = list(SMOKE)
    classes: set[str] = set()
    for name in paths:
        normalized = name.replace("\\", "/")
        low = normalized.lower()
        if low.startswith("tests/jularr.backend.tests/"):
            file = PurePosixPath(normalized).name
            if file.endswith("Tests.cs"):
                classes.add(PurePosixPath(file).stem)
            else:
                classes.update(SHARED)
        elif low.startswith(tuple(prefix.lower() for prefix in CORE_PREFIXES)) or low.startswith("database/"):
            # Match directory names and filename prefixes, not arbitrary substrings:
            # SpeechCatalog must not accidentally select every Library test.
            parts = PurePosixPath(low).parts
            stem = PurePosixPath(low).stem
            found = False
            if low.startswith("database/"):
                classes.update(("SqlContextTests", "DatabaseBaselineTests", "DatabaseMigrationTests"))
                found = True
            for words, test_classes in AREAS:
                if any(word in parts[:-1] or stem.startswith(word) for word in words):
                    classes.update(test_classes)
                    found = True
            if not found:
                classes.update(SHARED)
        elif low.startswith(tuple(prefix.lower() for prefix in CI_MACHINERY)):
            classes.update(SHARED)
            classes.update(name for _, test_classes in AREAS for name in test_classes)
        elif low.endswith((".props", ".targets")) or low in ("nuget.config", "jularr.sln"):
            classes.update(SHARED)

    # When a class is selected, its smoke methods are already included there.
    methods = [method for method in methods if method.split(".", 1)[0] not in classes]
    clauses = [method_clause(method) for method in methods]
    clauses += [f"FullyQualifiedName~{NAMESPACE}{name}." for name in sorted(classes)]
    return "|".join(clauses), tuple(sorted(classes))


def method_clause(method: str) -> str:
    # Exact: `~Class.Method` would also run Class.Method_Other, which then runs twice in Backend Full.
    return f"FullyQualifiedName={NAMESPACE}{method}"


def boot_filter() -> str:
    clauses = [method_clause(method) for method in SMOKE + BOOT_METHODS]
    clauses += [f"FullyQualifiedName~{NAMESPACE}{name}." for name in BOOT_CLASSES]
    return "|".join(clauses)


def boot_units() -> str:
    """The boot classes and methods for ci-shard-tests.py --exclude: Backend Full runs them in `smoke` only."""
    return ",".join(BOOT_CLASSES + SMOKE + BOOT_METHODS)


def self_test() -> None:
    cases = (
        (["src/Jularr.Backend/Logic/Acquisition/Search.cs"], "AcquisitionTests"),
        (["src/Jularr.Integrations/Logic/Sabnzbd/Client.cs"], "UsenetTests"),
        (["src/Jularr.Backend/Logic/Storage/Mount.cs"], "StorageTests"),
        (["src/Jularr.Backend/Service/Core/ServiceApi.cs"], "ServiceApiTests"),
        (["src/Jularr.Backend/Logic/Speech/Catalog.cs"], "SpeechDiscoveryTests"),
        (["database/Account.sql"], "AuthenticationTests"),
        (["tests/Jularr.Backend.Tests/NewFeatureTests.cs"], "NewFeatureTests"),
        (["tests/Jularr.Backend.Tests/Infrastructure/TestPostgres.cs"], "SqlContextTests"),
    )
    for paths, required in cases:
        _, selected = choose(paths)
        assert required in selected, (paths, selected)
    assert boot_filter().count("FullyQualifiedName=") == len(SMOKE) + len(BOOT_METHODS)
    assert boot_filter().count("FullyQualifiedName~") == len(BOOT_CLASSES)
    units = boot_units().split(",")
    assert len(units) == len(set(units)) == len(BOOT_CLASSES) + len(SMOKE) + len(BOOT_METHODS)
    assert all(f"{NAMESPACE}{unit}" in boot_filter() for unit in units), "smoke runs every unit the shards leave out"
    filter_text, classes = choose(["docs/README.md"])
    assert not classes and filter_text.count("FullyQualifiedName=") == len(SMOKE) and "~" not in filter_text
    filter_text, classes = choose(["src/Jularr.Frontend/Styles/app.scss"])
    assert not classes and filter_text.count("FullyQualifiedName=") == len(SMOKE)
    filters, classes = choose(["tests/Jularr.Backend.Tests/AuthenticationTests.cs"])
    assert classes == ("AuthenticationTests",) and "AuthenticationTests.Session_" not in filters
    _, classes = choose(["src/Jularr.Backend/Logic/Speech/SpeechCatalog.cs"])
    assert classes == ("SpeechDiscoveryTests", "SpeechTests"), classes
    _, classes = choose(["database/Account.sql"])
    assert classes == ("AuthenticationTests", "DatabaseBaselineTests", "DatabaseMigrationTests", "DeviceTests", "SqlContextTests"), classes
    for path in ("scripts/ci-shard-tests.py", ".github/workflows/backend-pr.yml", ".github/actions/install-tools/action.yml"):
        _, classes = choose([path])
        assert "AcquisitionTests" in classes and "ServiceApiTests" in classes and len(classes) >= 15, (path, classes)
    _, classes = choose([".github/workflows/frontend.yml", "scripts/verify-repository-hygiene.mjs"])
    assert not classes, classes


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        print("Backend test selection self-checks passed.")
        return 0
    if len(sys.argv) == 2 and sys.argv[1] == "--boot-filter":
        print(boot_filter())
        return 0
    if len(sys.argv) == 2 and sys.argv[1] == "--boot-units":
        print(boot_units())
        return 0
    as_json = len(sys.argv) == 4 and sys.argv[3] == "--json"
    if len(sys.argv) != 3 and not as_json:
        print("Usage: ci-select-backend-tests.py BASE_SHA HEAD_SHA [--json] | --self-test | --boot-filter | --boot-units", file=sys.stderr)
        return 2
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", "--diff-filter=ACMR", sys.argv[1], sys.argv[2]],
        text=True,
    ).splitlines()
    filter_text, classes = choose(changed)
    if as_json:
        # The smoke methods run in the prepare job; the shards split the selected classes.
        methods = [method for method in SMOKE if method.split(".", 1)[0] not in classes]
        print(json.dumps({"methods": "|".join(method_clause(method) for method in methods),
                          "classes": list(classes)}))
        return 0
    print(f"Selected additional backend test classes: {', '.join(classes) or '(none)'}", file=sys.stderr)
    print(filter_text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
