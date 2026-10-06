(() => {
  "use strict";

  const INSTALL_DISMISS_KEY = "jularr.pwa.installDismissedUntil";
  const INSTALL_DISMISS_MS = 7 * 24 * 60 * 60 * 1000;
  let deferredInstallPrompt = null;

  // Localized shell text is rendered by the server from the UI catalog
  // (pwa.* keys), so this script never keeps its own copy of UI strings.
  const shellText = (() => {
    try {
      return JSON.parse(
        document.getElementById("app-shell-text")?.textContent || "{}");
    } catch {
      return {};
    }
  })();
  const shellLabel = key => shellText[key] || key;

  const isStandalone = () =>
    window.matchMedia?.("(display-mode: standalone)")?.matches === true
    || window.navigator.standalone === true;

  const isAppleMobile = () => {
    const userAgent = navigator.userAgent || "";
    return /iPad|iPhone|iPod/.test(userAgent)
      || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  };

  const isMacSafari = () => {
    const userAgent = navigator.userAgent || "";
    return navigator.platform === "MacIntel"
      && navigator.maxTouchPoints <= 1
      && /Safari\//.test(userAgent)
      && !/(Chrome|Chromium|Edg|OPR)\//.test(userAgent);
  };

  const safeLocalStorageGet = key => {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  };

  const safeLocalStorageSet = (key, value) => {
    try {
      window.localStorage.setItem(key, value);
    } catch {
      // Storage can be unavailable in strict/private browser modes.
    }
  };

  const ensureMeta = (name, content) => {
    let element = document.head.querySelector('meta[name="' + name + '"]');
    if (!element) {
      element = document.createElement("meta");
      element.name = name;
      document.head.appendChild(element);
    }
    element.content = content;
  };

  const ensureLink = (rel, href) => {
    let element = document.head.querySelector('link[rel="' + rel + '"]');
    if (!element) {
      element = document.createElement("link");
      element.rel = rel;
      document.head.appendChild(element);
    }
    element.href = href;
  };

  const configurePlatformMetadata = () => {
    const viewport = document.head.querySelector('meta[name="viewport"]');
    if (viewport && !viewport.content.includes("viewport-fit=cover")) {
      viewport.content = viewport.content.trim().replace(/,+$/, "")
        + ", viewport-fit=cover";
    }

    ensureMeta("apple-mobile-web-app-capable", "yes");
    ensureMeta("apple-mobile-web-app-status-bar-style", "black-translucent");
    ensureMeta("apple-mobile-web-app-title", "Jularr");
    ensureLink("apple-touch-icon", "/icons/apple-touch-icon.png");

    document.documentElement.dataset.displayMode = isStandalone()
      ? "standalone"
      : "browser";
    document.documentElement.dataset.platform = isAppleMobile()
      ? "apple-mobile"
      : isMacSafari()
        ? "apple-desktop"
        : "standard";
  };

  const noticeHost = () => {
    let host = document.querySelector("[data-pwa-notice-host]");
    if (!host) {
      host = document.createElement("div");
      host.className = "pwa-notice-host";
      host.dataset.pwaNoticeHost = "";
      host.setAttribute("aria-live", "polite");
      document.body.appendChild(host);
    }
    return host;
  };

  const removeNotice = id => {
    document.getElementById(id)?.remove();
  };

  const showNotice = ({
    id,
    message,
    primaryLabel,
    onPrimary,
    secondaryLabel,
    onSecondary
  }) => {
    removeNotice(id);

    const notice = document.createElement("section");
    notice.id = id;
    notice.className = "pwa-notice";

    const text = document.createElement("p");
    text.textContent = message;
    notice.appendChild(text);

    const actions = document.createElement("div");
    actions.className = "pwa-notice-actions";

    if (secondaryLabel) {
      const secondary = document.createElement("button");
      secondary.type = "button";
      secondary.className = "button";
      secondary.textContent = secondaryLabel;
      secondary.addEventListener("click", async () => {
        if (onSecondary) {
          await onSecondary();
        }
        removeNotice(id);
      });
      actions.appendChild(secondary);
    }

    const primary = document.createElement("button");
    primary.type = "button";
    primary.className = "button button-primary";
    primary.textContent = primaryLabel;
    primary.addEventListener("click", async () => {
      primary.disabled = true;
      try {
        await onPrimary?.();
      } finally {
        primary.disabled = false;
      }
    });
    actions.appendChild(primary);

    notice.appendChild(actions);
    noticeHost().appendChild(notice);
    return notice;
  };

  const showToast = message => {
    const toast = document.createElement("div");
    toast.className = "toast pwa-runtime-toast";
    toast.textContent = message;
    document.body.appendChild(toast);
    window.setTimeout(() => toast.remove(), 3500);
  };

  const installDismissed = () => {
    const value = Number(safeLocalStorageGet(INSTALL_DISMISS_KEY));
    return Number.isFinite(value) && value > Date.now();
  };

  const dismissInstall = () => {
    safeLocalStorageSet(
      INSTALL_DISMISS_KEY,
      String(Date.now() + INSTALL_DISMISS_MS));
  };

  const offerBrowserInstall = () => {
    if (!deferredInstallPrompt || isStandalone() || installDismissed()) {
      return;
    }

    showNotice({
      id: "pwa-install-notice",
      message: shellLabel("pwa.install.prompt"),
      primaryLabel: shellLabel("pwa.install.action"),
      secondaryLabel: shellLabel("pwa.install.dismiss"),
      onSecondary: () => dismissInstall(),
      onPrimary: async () => {
        const prompt = deferredInstallPrompt;
        deferredInstallPrompt = null;
        removeNotice("pwa-install-notice");

        await prompt.prompt();
        const choice = await prompt.userChoice;
        if (choice?.outcome !== "accepted") {
          dismissInstall();
        }
      }
    });
  };

  const offerAppleInstallGuide = () => {
    const appleMobile = isAppleMobile();
    const macSafari = isMacSafari();

    if ((!appleMobile && !macSafari) || isStandalone() || installDismissed()) {
      return;
    }

    window.setTimeout(() => {
      if (isStandalone() || installDismissed()) {
        return;
      }

      showNotice({
        id: "pwa-install-notice",
        message: appleMobile
          ? shellLabel("pwa.install.appleMobile")
          : shellLabel("pwa.install.appleDesktop"),
        primaryLabel: shellLabel("pwa.install.acknowledge"),
        secondaryLabel: shellLabel("pwa.install.dismiss"),
        onSecondary: () => dismissInstall(),
        onPrimary: async () => {
          dismissInstall();
          removeNotice("pwa-install-notice");
        }
      });
    }, 2500);
  };

  const isVersionedShellAsset = url =>
    url
    && url.origin === window.location.origin
    && (
      (
        (url.pathname.startsWith("/css/") || url.pathname.startsWith("/js/"))
        && url.searchParams.has("v")
      )
      || url.pathname.startsWith("/build/")
    );

  const currentFingerprintedAssets = () => {
    const candidates = document.querySelectorAll(
      'link[rel="stylesheet"][href], link[rel="modulepreload"][href], script[src]');

    return [...new Set(Array.from(candidates)
      .map(element => element.href || element.src)
      .filter(Boolean)
      .map(value => {
        try {
          return new URL(value, window.location.href);
        } catch {
          return null;
        }
      })
      .filter(isVersionedShellAsset)
      .map(url => url.pathname + url.search))];
  };

  const serviceWorkerBuildKey = () => {
    // Page-specific scripts are intentionally cached through CACHE_CURRENT_ASSETS,
    // but must not change the registration URL. Otherwise navigating between
    // pages creates a new waiting worker and an endless update prompt.
    return document.documentElement.dataset.appVersion || "unknown";
  };

  const syncCurrentAssets = registration => {
    const worker = registration?.active || navigator.serviceWorker.controller;
    const urls = currentFingerprintedAssets();
    if (!worker || urls.length === 0) {
      return;
    }

    worker.postMessage({
      type: "CACHE_CURRENT_ASSETS",
      urls
    });
  };

  const registerServiceWorker = async () => {
    if (!("serviceWorker" in navigator)) {
      return;
    }

    try {
      const workerUrl =
        "/service-worker.js?v=" + encodeURIComponent(serviceWorkerBuildKey());
      const registration = await navigator.serviceWorker.register(
        workerUrl,
        { scope: "/", updateViaCache: "none" });

      syncCurrentAssets(registration);
      void navigator.serviceWorker.ready.then(syncCurrentAssets).catch(() => {});
    } catch {
      // PWA support is progressive enhancement and must never block Jularr.
    }
  };

  const copyText = async value => {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return true;
    }

    const textarea = document.createElement("textarea");
    textarea.value = value;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.appendChild(textarea);
    textarea.select();
    const copied = document.execCommand?.("copy") === true;
    textarea.remove();
    return copied;
  };

  const shareCurrentPage = async details => {
    const payload = {
      title: details?.title || document.title,
      text: details?.text || "",
      url: details?.url || window.location.href
    };

    if (navigator.share) {
      try {
        await navigator.share(payload);
        return true;
      } catch (error) {
        if (error?.name === "AbortError") {
          return false;
        }
      }
    }

    try {
      if (await copyText(payload.url)) {
        showToast(shellLabel("pwa.linkCopied"));
        return true;
      }
    } catch {
      // Clipboard fallback is optional.
    }

    return false;
  };

  const effectiveDuration = (root, video) => {
    if (Number.isFinite(video.duration) && video.duration > 0) {
      return video.duration;
    }

    const declared = Number(root.dataset.durationSeconds);
    return Number.isFinite(declared) && declared > 0 ? declared : null;
  };

  const clampPosition = (value, duration) =>
    Math.min(duration, Math.max(0, Number.isFinite(value) ? value : 0));

  const enhanceMediaSession = (root, video) => {
    if (!("mediaSession" in navigator)) {
      return;
    }

    const title = document.querySelector(".page-header h1")?.textContent?.trim()
      || document.title.replace(/\s+-\s+Jularr$/, "");
    const anime = document.querySelector(".page-header .back-link")?.textContent
      ?.replace(/^\s*←\s*/, "")
      ?.trim()
      || "Jularr";

    if ("MediaMetadata" in window) {
      navigator.mediaSession.metadata = new MediaMetadata({
        title,
        artist: anime,
        album: "Jularr",
        artwork: [
          { src: "/icons/jularr-192.png", sizes: "192x192", type: "image/png" },
          { src: "/icons/jularr-512.png", sizes: "512x512", type: "image/png" }
        ]
      });
    }

    const setHandler = (action, handler) => {
      try {
        navigator.mediaSession.setActionHandler(action, handler);
      } catch {
        // Browser exposes Media Session but not this individual action.
      }
    };

    const seekBy = delta => {
      const duration = effectiveDuration(root, video);
      const next = video.currentTime + delta;
      video.currentTime = duration
        ? clampPosition(next, duration)
        : Math.max(0, next);
    };

    setHandler("play", () => void video.play().catch(() => {}));
    setHandler("pause", () => video.pause());
    // System controls reuse the player's own seek actions (10 s back, 30 s forward) when available so
    // restarted live streams seek on the absolute media timeline. The increments never come from the
    // system's own seekOffset: every client seeks by the same canonical steps.
    const playerSeek = (action, fallbackDelta) => {
      const design = window.JularrPlayerDesign;
      if (design && root.querySelector("[data-player-controls]")) {
        design.dispatch(root, action);
      } else {
        seekBy(fallbackDelta);
      }
    };

    setHandler("seekbackward", () =>
      playerSeek("seekBack10", -Number(root.dataset.seekBackSeconds)));
    setHandler("seekforward", () =>
      playerSeek("seekForward10", Number(root.dataset.seekForwardSeconds)));
    setHandler("seekto", details => {
      const target = Number(details?.seekTime);
      if (!Number.isFinite(target)) {
        return;
      }

      const duration = effectiveDuration(root, video);
      const position = duration ? clampPosition(target, duration) : Math.max(0, target);
      if (details.fastSeek && typeof video.fastSeek === "function") {
        video.fastSeek(position);
      } else {
        video.currentTime = position;
      }
    });

    let lastPositionUpdate = 0;
    const updateState = force => {
      navigator.mediaSession.playbackState = video.paused ? "paused" : "playing";

      const now = performance.now();
      if (!force && now - lastPositionUpdate < 750) {
        return;
      }
      lastPositionUpdate = now;

      const duration = effectiveDuration(root, video);
      if (!duration || !Number.isFinite(video.currentTime)) {
        return;
      }

      try {
        navigator.mediaSession.setPositionState({
          duration,
          playbackRate: Number.isFinite(video.playbackRate) && video.playbackRate > 0
            ? video.playbackRate
            : 1,
          position: clampPosition(video.currentTime, duration)
        });
      } catch {
        // Position state is not available on every Media Session implementation.
      }
    };

    video.addEventListener("play", () => updateState(true));
    video.addEventListener("pause", () => updateState(true));
    video.addEventListener("timeupdate", () => updateState(false));
    video.addEventListener("durationchange", () => updateState(true));
    video.addEventListener("ratechange", () => updateState(true));
    video.addEventListener("loadedmetadata", () => updateState(true));
  };

  const enhanceWakeLock = (video) => {
    if (!navigator.wakeLock?.request) {
      return;
    }

    let lock = null;

    const release = async () => {
      const active = lock;
      lock = null;
      if (active) {
        try {
          await active.release();
        } catch {
          // The browser may already have released the lock.
        }
      }
    };

    const acquire = async () => {
      if (lock || video.paused || video.ended || document.visibilityState !== "visible") {
        return;
      }

      try {
        lock = await navigator.wakeLock.request("screen");
        lock.addEventListener("release", () => {
          lock = null;
        }, { once: true });
      } catch {
        lock = null;
      }
    };

    video.addEventListener("play", () => void acquire());
    video.addEventListener("pause", () => void release());
    video.addEventListener("ended", () => void release());
    window.addEventListener("pagehide", () => void release());
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") {
        void acquire();
      }
    });
  };

  const enhanceEpisodePlayer = root => {
    if (!(root instanceof HTMLElement) || root.dataset.pwaEnhanced === "true") {
      return;
    }

    const video = root.querySelector("[data-playback-video]");
    const stage = root.querySelector("[data-video-stage]") || video?.parentElement;
    if (!(video instanceof HTMLVideoElement) || !(stage instanceof HTMLElement)) {
      return;
    }

    root.dataset.pwaEnhanced = "true";
    enhanceMediaSession(root, video);
    enhanceWakeLock(video);

    // Picture-in-picture and full screen belong to the player chrome (player-chrome.js).
  };

  const enhanceExistingPlayers = () => {
    document.querySelectorAll("[data-episode-player]")
      .forEach(enhanceEpisodePlayer);
  };

  window.addEventListener("beforeinstallprompt", event => {
    event.preventDefault();
    deferredInstallPrompt = event;
    offerBrowserInstall();
  });

  window.addEventListener("appinstalled", () => {
    deferredInstallPrompt = null;
    removeNotice("pwa-install-notice");
    safeLocalStorageSet(INSTALL_DISMISS_KEY, "0");
    document.documentElement.dataset.displayMode = "standalone";
  });

  window.addEventListener("offline", () =>
    showToast(shellLabel("pwa.offline")));
  window.addEventListener("online", () =>
    showToast(shellLabel("pwa.online")));

  // Ctrl/Cmd+K focuses the global search. Discover owns a search of its own and leaves the header
  // field out, so the shortcut lands there instead; a hidden field (a phone without it) is skipped.
  document.addEventListener("keydown", event => {
    if (event.key.toLowerCase() !== "k" || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) {
      return;
    }

    const target = [...document.querySelectorAll("[data-dc-search], [data-app-search]")]
      .find(field => field.getClientRects().length > 0);
    if (target) {
      event.preventDefault();
      target.focus();
      target.select();
    }
  });

  // Admin and Settings reopen on the page last used inside them. The server
  // decides which section is expanded; this only remembers the child link.
  const NAV_LAST_PAGE_KEY = "jularr.nav.lastPage.";
  document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) {
      return;
    }

    const child = event.target.closest("[data-nav-section] .nav-children a[href]");
    if (child) {
      const section = child.closest("[data-nav-section]").dataset.navSection;
      safeLocalStorageSet(NAV_LAST_PAGE_KEY + section, child.getAttribute("href"));
      return;
    }

    const anchor = event.target.closest("a[data-nav-section-link]");
    if (!anchor || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }

    const last = safeLocalStorageGet(NAV_LAST_PAGE_KEY + anchor.dataset.navSectionLink);
    if (last && last.startsWith("/") && !last.startsWith("//")) {
      event.preventDefault();
      window.location.assign(last);
    }
  });

  document.addEventListener("submit", event => {
    if (event.target instanceof HTMLFormElement
        && event.target.matches("[data-offline-logout]")) {
      try {
        localStorage.removeItem("jularr.activeProfile");
      } catch {
        // Ignore unavailable local storage.
      }
    }
  });

  const initialize = () => {
    configurePlatformMetadata();
    enhanceExistingPlayers();
    offerAppleInstallGuide();
    void registerServiceWorker();

    const observer = new MutationObserver(() => enhanceExistingPlayers());
    observer.observe(document.body, { childList: true, subtree: true });
  };

  window.JularrPwa = Object.freeze({
    isStandalone,
    shareCurrentPage
  });

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initialize, { once: true });
  } else {
    initialize();
  }
})();
