#!/usr/bin/env python3
"""Choose a bounded, change-aware backend regression set for pull requests.

All backend test code is compiled separately. This selector adds focused test
classes to a small invariant smoke suite. Full CI still owns exhaustive runs.
"""
from __future__ import annotations

from pathlib import PurePosixPath
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

# A path may belong to several areas (e.g. downloads + storage). The mappings
# are deliberately plain to make changes auditable in code review. Keep them
# aligned with tests/Jularr.Backend.Tests and docs/CODEMAP.md.
AREAS = (
    (("acquisition", "download", "wanted", "newznab", "sabnzbd", "sonarr",
      "radarr", "lidarr", "readarr", "usenet", "torrent", "arrplus"),
     ("AcquisitionTests", "UsenetTests", "ManualImportLogicTests")),
    (("storage", "mount", "smb", "filesystem", "fileimport"),
     ("StorageTests", "BackupTests")),
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
                classes.add("SqlContextTests")
                found = True
            for words, test_classes in AREAS:
                if any(word in parts[:-1] or stem.startswith(word) for word in words):
                    classes.update(test_classes)
                    found = True
            if not found:
                classes.update(SHARED)
        elif low.endswith((".props", ".targets")) or low in ("nuget.config", "jularr.sln"):
            classes.update(SHARED)

    # When a class is selected, its smoke methods are already included there.
    methods = [method for method in methods if method.split(".", 1)[0] not in classes]
    clauses = [f"FullyQualifiedName~{NAMESPACE}{method}" for method in methods]
    clauses += [f"FullyQualifiedName~{NAMESPACE}{name}." for name in sorted(classes)]
    return "|".join(clauses), tuple(sorted(classes))


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
    filter_text, classes = choose(["docs/README.md"])
    assert not classes and filter_text.count("FullyQualifiedName~") == len(SMOKE)
    filter_text, classes = choose(["src/Jularr.Frontend/Styles/app.scss"])
    assert not classes and filter_text.count("FullyQualifiedName~") == len(SMOKE)
    filters, classes = choose(["tests/Jularr.Backend.Tests/AuthenticationTests.cs"])
    assert classes == ("AuthenticationTests",) and "AuthenticationTests.Session_" not in filters
    _, classes = choose(["src/Jularr.Backend/Logic/Speech/SpeechCatalog.cs"])
    assert classes == ("SpeechDiscoveryTests", "SpeechTests"), classes
    _, classes = choose(["database/Account.sql"])
    assert classes == ("AuthenticationTests", "DeviceTests", "SqlContextTests"), classes


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        self_test()
        print("Backend test selection self-checks passed.")
        return 0
    if len(sys.argv) != 3:
        print("Usage: ci-select-backend-tests.py BASE_SHA HEAD_SHA", file=sys.stderr)
        return 2
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", "--diff-filter=ACMR", sys.argv[1], sys.argv[2]],
        text=True,
    ).splitlines()
    filter_text, classes = choose(changed)
    print(f"Selected additional backend test classes: {', '.join(classes) or '(none)'}", file=sys.stderr)
    print(filter_text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
