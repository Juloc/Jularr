#!/bin/sh
# A browser for the sources that put one in front of their pages.
#
# The image of Jularr stays slim: the browser and its libraries go into the container, the profile of the
# browser goes into the instance's data volume. The source's check is passed once and stays passed, as long
# as the volume lives.
#
#   install   adds the browser and its libraries (needs root)
#   run       starts the browser with a fixed port for Jularr to speak to
#
# Jularr reads the browser with Browser:RemoteAddress (for example http://127.0.0.1:9222).

set -eu

home="${JULARR_BROWSER_HOME:-/data/browser}"
port="${JULARR_BROWSER_PORT:-9222}"
forward="${JULARR_BROWSER_FORWARD_PORT:-9223}"
display="${JULARR_BROWSER_DISPLAY:-:99}"

program() {
    case "$(uname -m)" in
        x86_64) echo "/opt/google/chrome/chrome" ;;
        aarch64) echo "$home/chromium/chrome-linux-arm64/chrome" ;;
        *) echo "no browser for $(uname -m)" >&2; return 1 ;;
    esac
}

install_browser() {
    apt-get update

    # The names of the libraries differ between the distributions; only the ones that exist are taken.
    wanted="ca-certificates curl unzip xvfb socat libasound2 libatk-bridge2.0-0 libatk1.0-0 libcups2 libdbus-1-3 \
        libdrm2 libgbm1 libglib2.0-0 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 \
        libxext6 libxfixes3 libxkbcommon0 libxrandr2 libxshmfence1 fonts-liberation"
    packages=""
    for package in $wanted; do
        if apt-cache show "$package" > /dev/null 2>&1; then
            packages="$packages $package"
        fi
    done
    apt-get install -y --no-install-recommends $packages

    case "$(uname -m)" in
        x86_64)
            # The browser of Google for this architecture.
            curl -fsSL "https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb" -o /tmp/chrome.deb
            apt-get install -y /tmp/chrome.deb
            rm -f /tmp/chrome.deb
            ;;
        aarch64)
            # Google builds no package for this architecture; a Chromium of the same version is used.
            revision="1243"
            curl -fsSL "https://cdn.playwright.dev/dbazure/download/playwright/builds/chromium/$revision/chromium-linux-arm64.zip" -o /tmp/chromium.zip
            mkdir -p "$home/chromium"
            unzip -q -o /tmp/chromium.zip -d "$home/chromium"
            rm -f /tmp/chromium.zip
            ;;
        *)
            echo "no browser for $(uname -m)" >&2
            exit 1
            ;;
    esac

    chmod +x "$(program)"
    "$(program)" --version
}

run_browser() {
    browser="$(program)"
    if [ ! -x "$browser" ]; then
        echo "no browser in $browser; run '$0 install' first" >&2
        exit 1
    fi

    # Without a screen of its own the browser needs a virtual one.
    if ! xdpyinfo -display "$display" > /dev/null 2>&1; then
        Xvfb "$display" -screen 0 1280x900x24 -nolisten tcp &
        sleep 2
    fi

    # A lock of an earlier run on another machine would keep the browser from starting.
    mkdir -p "$home/profile"
    rm -f "$home/profile/SingletonLock" "$home/profile/SingletonCookie" "$home/profile/SingletonSocket"

    DISPLAY="$display" "$browser" --remote-debugging-port="$port" --user-data-dir="$home/profile" \
        --no-first-run --no-default-browser-check --no-sandbox --enable-unsafe-swiftshader --use-angle=swiftshader \
        --window-size=1280,900 about:blank &

    # The browser answers on its own loopback; a second port leads to it, so the browser can be reached
    # from outside the container as well.
    if command -v socat > /dev/null 2>&1; then
        socat "TCP-LISTEN:$forward,fork,reuseaddr" "TCP:127.0.0.1:$port" &
    fi

    # The port answers when the browser is ready.
    for _ in $(seq 1 60); do
        if curl -fsS "http://127.0.0.1:$port/json/version" > /dev/null 2>&1; then
            echo "the browser answers on port $port"
            break
        fi
        sleep 1
    done

    # The script stays with the browser, so it lives as long as the container does.
    wait
}

case "${1:-}" in
    install) install_browser ;;
    run) run_browser ;;
    *) echo "usage: $0 install|run" >&2; exit 1 ;;
esac
