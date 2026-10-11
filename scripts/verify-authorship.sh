#!/usr/bin/env bash
set -euo pipefail

required_identity='Juloc <juli.hiresch@gmail.com>'
forbidden_trailer_pattern='^[[:space:]]*co-authored-by:|^[[:space:]]*(author|generated-by|assisted-by):.*(claude|julocae|chatgpt|codex|copilot|anthropic|openai)'

failures=0

verify_identity() {
  local label="$1"
  local value="$2"

  if [[ "${value%%>*}>" != "$required_identity" ]]; then
    echo "Authorship verification failed: ${label} must be exactly ${required_identity}." >&2
    failures=1
  fi
}

fail_if_forbidden_trailer() {
  local message="$1"

  if printf '%s\n' "$message" | grep -Eiq "$forbidden_trailer_pattern"; then
    echo "Authorship verification failed: commit messages must not contain co-author or AI attribution trailers." >&2
    failures=1
  fi
}

verify_current_identity_and_message() {
  local message_file="$1"

  test -f "$message_file" || { echo "Authorship verification failed: commit message file was not found." >&2; exit 2; }
  verify_identity "author identity" "$(git var GIT_AUTHOR_IDENT)"
  verify_identity "committer identity" "$(git var GIT_COMMITTER_IDENT)"
  fail_if_forbidden_trailer "$(<"$message_file")"
}

verify_commit_range() {
  local range="$1"
  local commit
  local revisions=("$range")

  if [[ -n "${2:-}" ]]; then
    revisions+=("^$2")
  fi

  while IFS= read -r commit; do
    verify_identity "author of ${commit}" "$(git show -s --format='%an <%ae>' "$commit")"
    verify_identity "committer of ${commit}" "$(git show -s --format='%cn <%ce>' "$commit")"
    fail_if_forbidden_trailer "$(git show -s --format='%B' "$commit")"
  done < <(git rev-list "${revisions[@]}")
}

case "${1:-}" in
  --message-file)
    test "$#" -eq 2 || { echo "Usage: $0 --message-file <path>" >&2; exit 2; }
    verify_current_identity_and_message "$2"
    ;;
  --range)
    if [[ "$#" -eq 4 && "$3" == --exclude && "$4" =~ ^[0-9a-f]{40}$ ]]; then
      verify_commit_range "$2" "$4"
    elif [[ "$#" -eq 2 ]]; then
      verify_commit_range "$2"
    else
      echo "Usage: $0 --range <git-range> [--exclude <existing-base-sha>]" >&2
      exit 2
    fi
    ;;
  *)
    echo "Usage: $0 --message-file <path> | --range <git-range>" >&2
    exit 2
    ;;
esac

exit "$failures"
