#!/bin/sh
# Jularr's managed, unprivileged Chrome for Testing installation. The app owns only this data subtree.
set -eu
umask 077
home="${JULARR_BROWSER_HOME:-/data/browser}"
case "$home" in /*/browser) ;; *) echo 'Refusing browser home outside a dedicated absolute browser folder' >&2; exit 2 ;; esac
case "$(uname -m)" in
  x86_64) platform=linux64; folder=chrome-linux64; sha=e6c94490f0b6b337225aeb2d2a5034858516b7017356980ba94f5176d8774a50 ;;
  aarch64) platform=linux-arm64; folder=chrome-linux-arm64; sha=9e37000e3ee2731e45e475a6a2eac587d3c0ee5f8aef7cab5211ecd52cf3fb7b ;;
  *) echo 'Unsupported browser architecture' >&2; exit 2 ;;
esac
version=156.0.8078.47
runtime="$home/runtime"
chrome="$runtime/$folder/chrome"
install_browser() {
  if [ -L "$home" ] || [ -L "$runtime" ]; then echo 'Unsafe browser directory' >&2; exit 2; fi
  mkdir -p "$home"
  # A SIGKILL or power loss can leave an old staging directory, even when install succeeded.
  for dir in "$home"/.install.*; do [ ! -e "$dir" ] || rm -rf "$dir"; done
  if [ -x "$chrome" ] && [ "$(cat "$runtime/.version" 2>/dev/null || true)" = "$version" ]; then exit 0; fi
  stage="$(mktemp -d "$home/.install.XXXXXXXX")"
  trap 'rm -rf "$stage"' EXIT HUP INT TERM
  curl --fail --location --proto '=https' --proto-redir '=https' --tlsv1.2 --retry 2 --connect-timeout 20 --max-time 240 --max-filesize 300000000 \
    "https://storage.googleapis.com/chrome-for-testing-public/$version/$platform/$folder.zip" -o "$stage/browser.zip"
  printf '%s  %s\n' "$sha" "$stage/browser.zip" | sha256sum --check --status
  unzip -Z1 "$stage/browser.zip" | awk -v root="$folder/" 'index($0,root)!=1 || index($0,"..") || substr($0,1,1)=="/" { bad=1 } END { exit bad }'
  mkdir -p "$stage/extract"
  unzip -q "$stage/browser.zip" -d "$stage/extract"
  if [ ! -f "$stage/extract/$folder/chrome" ]; then echo 'Missing browser executable' >&2; exit 2; fi
  chmod 0755 "$stage/extract/$folder/chrome"
  # Validate the downloaded executable before replacing a working browser.
  "$stage/extract/$folder/chrome" --version
  printf '%s\n' "$version" > "$stage/extract/.version"
  if [ -e "$runtime" ]; then mv "$runtime" "$stage/previous"; fi
  if ! mv "$stage/extract" "$runtime"; then
    if [ -d "$stage/previous" ]; then mv "$stage/previous" "$runtime"; fi
    exit 1
  fi
  "$chrome" --version
}
remove_browser() {
  [ ! -L "$home" ] || { echo 'Unsafe browser home' >&2; exit 2; }
  [ ! -L "$runtime" ] || { echo 'Unsafe browser runtime' >&2; exit 2; }
  # The entire browser-owned directory belongs to this optional module, including hidden
  # state directories used by Chrome. Preserve a directory that is itself a volume mount.
  if [ -d "$home" ]; then
    find "$home" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
    rmdir "$home" 2>/dev/null || true
  fi
}
run_browser() {
  [ -x "$chrome" ] || { echo 'Browser not installed' >&2; exit 2; }
  display="${JULARR_BROWSER_DISPLAY:-:99}"
  port="${JULARR_BROWSER_PORT:-9222}"
  if ! xdpyinfo -display "$display" >/dev/null 2>&1; then
    Xvfb "$display" -screen 0 1280x900x24 -nolisten tcp &
    xvfb=$!
    sleep 1
  else
    xvfb=
  fi
  mkdir -p "$home/profile" "$home/cache" "$home/config" "$home/tmp" "$home/share" "$home/state"
  HOME="$home" XDG_CACHE_HOME="$home/cache" XDG_CONFIG_HOME="$home/config" XDG_DATA_HOME="$home/share" \
    XDG_STATE_HOME="$home/state" TMPDIR="$home/tmp" DISPLAY="$display" "$chrome" --remote-debugging-port="$port" --remote-debugging-address=127.0.0.1 \
    --user-data-dir="$home/profile" --no-first-run --no-default-browser-check --no-sandbox --disable-dev-shm-usage \
    --enable-unsafe-swiftshader --use-angle=swiftshader --window-size=1280,900 about:blank &
  browser=$!
  trap 'kill "$browser" 2>/dev/null || true; if [ -n "$xvfb" ]; then kill "$xvfb" 2>/dev/null || true; fi' EXIT HUP INT TERM
  wait "$browser"
}
case "${1:-}" in
  install) install_browser ;;
  uninstall) remove_browser ;;
  run) run_browser ;;
  *) echo 'usage: jularr-browser install|uninstall|run' >&2; exit 2 ;;
esac
