#!/usr/bin/env bash
# Prints "<version> <release tag>" of the highest signed Jularr-TV-<version>.apk attached to a published
# stable release, or nothing if there is none. The TV app reads only /releases/latest, so the Release and
# TV Release workflows both use this to keep the newest APK on the newest release.
# Usage: newest-tv-apk.sh [release tag to ignore]; needs GH_TOKEN and GH_REPO.
set -euo pipefail

gh api --paginate "repos/${GH_REPO}/releases?per_page=100" |
  jq -r --arg skip "${1:-}" '
    .[] | select(.draft == false and .prerelease == false and .tag_name != $skip) | .tag_name as $release |
    .assets[]? | select(.name | test("^Jularr-TV-[0-9]+[.][0-9]+[.][0-9]+[.]apk$")) |
    [(.name | ltrimstr("Jularr-TV-") | rtrimstr(".apk")), $release] | join(" ")' |
  sort -k1,1V | tail -n 1
