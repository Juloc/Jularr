#!/bin/sh
# Optional browser sidecar. Install during image build; run as the unprivileged app user.
set -eu

home="${JULARR_BROWSER_HOME:-/data/browser}"
port="${JULARR_BROWSER_PORT:-9222}"
forward="${JULARR_BROWSER_FORWARD_PORT:-9223}"
forward_bind="${JULARR_BROWSER_FORWARD_BIND:-127.0.0.1}"
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
    wanted="ca-certificates curl unzip xvfb nginx libasound2 libatk-bridge2.0-0 libatk1.0-0 libcups2 libdbus-1-3 \
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
            curl -fsSL "https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb" -o /tmp/chrome.deb
            apt-get install -y /tmp/chrome.deb
            rm -f /tmp/chrome.deb
            ;;
        aarch64)
            revision="1243"
            curl -fsSL "https://cdn.playwright.dev/dbazure/download/playwright/builds/chromium/$revision/chromium-linux-arm64.zip" -o /tmp/chromium.zip
            mkdir -p "$home/chromium"
            unzip -q -o /tmp/chromium.zip -d "$home/chromium"
            rm -f /tmp/chromium.zip
            ;;
        *) exit 1 ;;
    esac
    chmod +x "$(program)"
    "$(program)" --version
    rm -rf /var/lib/apt/lists/*
}

run_browser() {
    browser="$(program)"
    if [ ! -x "$browser" ]; then
        echo "browser not installed: $browser" >&2
        exit 1
    fi
    command -v nginx >/dev/null 2>&1 || { echo "nginx is required for the browser proxy" >&2; exit 1; }

    mkdir -p "$home/profile"
    Xvfb "$display" -screen 0 1280x900x24 -nolisten tcp &
    xvfb_pid=$!
    sleep 2

    rm -f "$home/profile/SingletonLock" "$home/profile/SingletonCookie" "$home/profile/SingletonSocket"
    set --
    if [ "${JULARR_BROWSER_NO_SANDBOX:-0}" = "1" ]; then
        set -- "$@" --no-sandbox
    fi
    DISPLAY="$display" "$browser" --remote-debugging-port="$port" --user-data-dir="$home/profile" \
        --no-first-run --no-default-browser-check --window-size=1280,900 "$@" about:blank &
    browser_pid=$!

    # Chrome only serves local DevTools. The proxy normalizes Host and supports WebSocket upgrades.
    cat > "$home/nginx.conf" <<EOF
pid $home/nginx.pid;
error_log $home/nginx.log warn;
events { worker_connections 64; }
http {
    access_log off;
    server {
        listen $forward_bind:$forward;
        location / {
            proxy_pass http://127.0.0.1:$port;
            proxy_http_version 1.1;
            proxy_set_header Host 127.0.0.1:$port;
            proxy_set_header Upgrade \$http_upgrade;
            proxy_set_header Connection "upgrade";
            proxy_read_timeout 3600s;
            proxy_buffering off;
        }
    }
}
EOF
    nginx -p "$home/" -c "$home/nginx.conf" -g "daemon off;" &
    proxy_pid=$!

    cleanup() {
        kill "$browser_pid" "$proxy_pid" "$xvfb_pid" 2>/dev/null || true
        wait "$browser_pid" "$proxy_pid" "$xvfb_pid" 2>/dev/null || true
    }
    trap cleanup EXIT
    trap 'exit 0' INT TERM

    ready=0
    for _ in $(seq 1 60); do
        if ! kill -0 "$browser_pid" 2>/dev/null || ! kill -0 "$proxy_pid" 2>/dev/null; then
            break
        fi
        if curl -fsS "http://127.0.0.1:$forward/json/version" >/dev/null 2>&1; then
            ready=1
            break
        fi
        sleep 1
    done
    if [ "$ready" -ne 1 ]; then
        echo "browser/proxy failed to become ready" >&2
        exit 1
    fi

    # Any child exit tears down the container, so Docker's restart policy handles crashes.
    while kill -0 "$browser_pid" 2>/dev/null && kill -0 "$proxy_pid" 2>/dev/null && kill -0 "$xvfb_pid" 2>/dev/null; do
        sleep 2
    done
    echo "browser stack exited" >&2
    exit 1
}

case "${1:-}" in
    install) install_browser ;;
    run) run_browser ;;
    *) echo "usage: $0 install|run" >&2; exit 1 ;;
esac
