(() => {
    const shell = document.querySelector("[data-novel-reader][data-reader-personalization]");
    if (!shell) return;

    const settingsElement = shell.querySelector("[data-reader-settings-json]");
    const settingsForm = shell.querySelector("[data-reader-settings-form]");
    const resetForm = shell.querySelector("[data-reader-reset-form]");
    const bookmarkAppearanceForm = shell.querySelector("[data-bookmark-appearance-endpoint]");
    const bookmarkForm = shell.querySelector("[data-bookmark-form]");
    const progressForm = shell.querySelector("[data-progress-form]");
    const content = shell.querySelector("[data-reader-content]");
    const pageControls = shell.querySelector("[data-reader-page-controls]");
    const pageNumber = shell.querySelector("[data-reader-page-number]");
    const pageBookmarks = shell.querySelector("[data-page-bookmarks]");
    const autoScrollButton = shell.querySelector("[data-reader-autoscroll-toggle]");
    const wakeLockButton = shell.querySelector("[data-reader-wake-lock-toggle]");
    const immersiveButton = shell.querySelector("[data-reader-immersive-toggle]");
    const overrideState = shell.querySelector("[data-reader-override-state]");
    const settingsPanel = shell.querySelector("[data-reader-settings-panel]");
    const backgroundSelect = shell.querySelector("[data-reader-background-select]");
    const genreSelect = shell.querySelector("[data-reader-genre-select]");
    const toast = shell.querySelector("[data-reader-toast]");
    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const pageNumberOverlay = shell.querySelector("[data-novel-page-number]");
    const frame = shell.hasAttribute("data-reader-frame");

    if (!settingsElement || !settingsForm || !content) return;

    // UI text from the catalog (novels.reader.*); English fallbacks only cover a
    // missing bundle.
    let text = {};
    try {
        text = JSON.parse(shell.querySelector("[data-novel-reader-text]")?.textContent || "{}") || {};
    } catch {
        text = {};
    }
    const t = (key, fallback, values = {}) => {
        let value = text["novels.reader." + key] || fallback;
        for (const [name, replacement] of Object.entries(values)) {
            value = value.replaceAll("{" + name + "}", String(replacement));
        }
        return value;
    };

    let state;
    try {
        state = JSON.parse(settingsElement.textContent || "{}");
    } catch {
        return;
    }

    let pageCount = 1;
    let currentPage = 0;
    let resizeTimer = null;
    let settledPagedAnchor = null;
    let autoScrollFrame = null;
    let autoScrollLastTime = null;
    let autoScrollRunning = false;
    let readerWakeLock = null;
    let keepAwake = true;
    let toastTimer = null;
    let saveQueue = Promise.resolve();

    const bookmarkState = new Map();

    const clamp = (value, min, max) =>
        Math.min(max, Math.max(min, value));

    const cssEscape = value =>
        window.CSS?.escape ? window.CSS.escape(value) : String(value).replace(/["\\]/g, "\\$&");

    const showToast = message => {
        if (!toast) return;
        clearTimeout(toastTimer);
        toast.textContent = message;
        toast.hidden = false;
        toastTimer = setTimeout(() => {
            toast.hidden = true;
        }, 1800);
    };

    const profileId = document.body?.dataset.profileId || "unknown";
    const wakeLockStorageKey = `anilingo.profile.${profileId}.novel.keepAwake`;

    const readKeepAwakePreference = () => {
        try {
            const stored = localStorage.getItem(wakeLockStorageKey);
            return stored == null ? true : stored === "true";
        } catch {
            return true;
        }
    };

    const writeKeepAwakePreference = value => {
        try {
            localStorage.setItem(wakeLockStorageKey, String(value));
        } catch {
            // Reader controls remain usable when storage is unavailable.
        }
    };

    const wakeLockSupported = () =>
        typeof navigator.wakeLock?.request === "function";

    const syncWakeLockButton = () => {
        if (!wakeLockButton) return;
        const supported = wakeLockSupported();
        // Unsupported browsers get no control at all instead of a dead one.
        wakeLockButton.hidden = !supported;
        const active = supported && keepAwake ? "true" : "false";
        wakeLockButton.setAttribute("aria-pressed", active);
        wakeLockButton.setAttribute("aria-checked", active);
        wakeLockButton.classList.toggle("is-active", supported && keepAwake);
        wakeLockButton.dataset.wakeLockActive = String(Boolean(readerWakeLock));
    };

    const releaseReaderWakeLock = async () => {
        const active = readerWakeLock;
        readerWakeLock = null;
        syncWakeLockButton();
        if (!active) return;
        try {
            await active.release();
        } catch {
            // The browser may already have released the lock.
        }
    };

    const acquireReaderWakeLock = async () => {
        if (!keepAwake ||
            !wakeLockSupported() ||
            readerWakeLock ||
            document.visibilityState !== "visible") {
            syncWakeLockButton();
            return;
        }

        try {
            const requested = await navigator.wakeLock.request("screen");
            readerWakeLock = requested;
            requested.addEventListener("release", () => {
                if (readerWakeLock === requested) {
                    readerWakeLock = null;
                }
                syncWakeLockButton();
            }, { once: true });
        } catch {
            readerWakeLock = null;
        }
        syncWakeLockButton();
    };

    const toggleReaderWakeLock = async () => {
        if (!wakeLockSupported()) {
            showToast(t("wakeLockUnsupported", "This browser cannot keep the screen on."));
            return;
        }

        keepAwake = !keepAwake;
        writeKeepAwakePreference(keepAwake);
        if (keepAwake) {
            await acquireReaderWakeLock();
            if (!readerWakeLock) {
                showToast(t("wakeLockFailed", "The screen could not be kept on."));
            }
        } else {
            await releaseReaderWakeLock();
        }
        syncWakeLockButton();
    };

    const fullscreenElement = () =>
        document.fullscreenElement || document.webkitFullscreenElement || null;

    const immersiveFallbackActive = () =>
        shell.classList.contains("reader-immersive-fallback");

    const syncImmersiveButton = () => {
        if (!immersiveButton) return;
        const active = fullscreenElement() === shell || immersiveFallbackActive();
        immersiveButton.setAttribute("aria-pressed", active ? "true" : "false");
        immersiveButton.classList.toggle("is-active", active);
        const label = active
            ? t("exitFullscreen", "Exit fullscreen")
            : t("fullscreen", "Fullscreen");
        immersiveButton.title = label;
        immersiveButton.setAttribute("aria-label", label);
        shell.classList.toggle("reader-fullscreen", active);
    };

    const exitFullscreen = async () => {
        if (typeof document.exitFullscreen === "function") {
            await document.exitFullscreen();
            return true;
        }
        if (typeof document.webkitExitFullscreen === "function") {
            document.webkitExitFullscreen();
            return true;
        }
        return false;
    };

    const requestReaderFullscreen = async () => {
        if (typeof shell.requestFullscreen === "function") {
            await shell.requestFullscreen();
            return true;
        }
        if (typeof shell.webkitRequestFullscreen === "function") {
            shell.webkitRequestFullscreen();
            return true;
        }
        return false;
    };

    const toggleImmersiveReader = async () => {
        if (fullscreenElement() === shell) {
            try {
                await exitFullscreen();
            } catch {
                // Browser-specific fullscreen exits can reject without user-visible impact.
            }
            return;
        }

        if (immersiveFallbackActive()) {
            shell.classList.remove("reader-immersive-fallback", "reader-focus");
            syncImmersiveButton();
            return;
        }

        try {
            if (await requestReaderFullscreen()) {
                syncImmersiveButton();
                return;
            }
        } catch {
            // Fall through to the in-page focus mode.
        }

        shell.classList.add("reader-immersive-fallback", "reader-focus");
        syncImmersiveButton();
        showToast(t("fullscreenFallback", "This browser cannot hide its toolbars completely."));
    };

    const tokenFrom = form =>
        form?.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const setFormValue = (data, key, value) => {
        data.set(key, value == null ? "" : String(value));
    };

    const fillSettingsData = (data, source = state) => {
        setFormValue(data, "ReadingMode", source.readingMode);
        setFormValue(data, "PageTransition", source.pageTransition);
        setFormValue(data, "TwoPageSpread", source.twoPageSpread);
        setFormValue(data, "AutoScrollSpeed", source.autoScrollSpeed);
        setFormValue(data, "FontFamily", source.fontFamily);
        setFormValue(data, "FontSizeRem", source.fontSizeRem);
        setFormValue(data, "LineHeight", source.lineHeight);
        setFormValue(data, "ParagraphSpacingEm", source.paragraphSpacingEm);
        setFormValue(data, "TextWidthPx", source.textWidthPx);
        setFormValue(data, "TextAlignment", source.textAlignment);
        setFormValue(data, "ChapterStyle", source.chapterStyle);
        setFormValue(data, "PaperStyle", source.paperStyle);
        setFormValue(data, "GenreArtworkEnabled", source.genreArtworkEnabled);
        setFormValue(data, "GenreTheme", source.genreTheme);
        setFormValue(data, "BackgroundAssetId", source.backgroundAssetId || "auto");
        setFormValue(data, "BackgroundIntensity", source.backgroundIntensity);
        setFormValue(data, "BackgroundMotionMode", source.backgroundMotionMode || "auto");
        setFormValue(data, "ThemeEffectStrength", source.themeEffectStrength);
        setFormValue(data, "ThemeBrightness", source.themeBrightness);
        setFormValue(data, "ThemeContrast", source.themeContrast);
        setFormValue(data, "ThemeSaturation", source.themeSaturation);
        setFormValue(data, "ThemeBlurPx", source.themeBlurPx);
        setFormValue(data, "ThemeVignetteStrength", source.themeVignetteStrength);
        setFormValue(data, "ThemeGrainStrength", source.themeGrainStrength);
        setFormValue(data, "ThemeTextBackdropStrength", source.themeTextBackdropStrength);
        setFormValue(data, "ThemeParallaxStrength", source.themeParallaxStrength);
        setFormValue(data, "ThemeTintStrength", source.themeTintStrength);
        setFormValue(data, "BookmarkStyle", source.bookmarkStyle);
        setFormValue(data, "BookmarkColor", source.bookmarkColor);
        setFormValue(data, "Hyphenation", source.hyphenation);
        setFormValue(data, "ParagraphIndent", source.paragraphIndent);
        setFormValue(data, "ShowPageNumbers", source.showPageNumbers);
        setFormValue(data, "ShowIllustrations", source.showIllustrations);
        setFormValue(data, "AutoContinueChapters", source.autoContinueChapters);
    };

    const postSettings = async (
        scope,
        changedKey,
        source = state,
        applyResponse = true) => {
        const data = new FormData(settingsForm);
        fillSettingsData(data, source);
        setFormValue(data, "scope", scope);
        if (changedKey) setFormValue(data, "changedKey", changedKey);

        const response = await fetch(settingsForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) {
            throw new Error(t("settingsSaveFailed", "The settings could not be saved."));
        }

        const payload = await response.json();
        if (payload?.settings && applyResponse) {
            state = payload.settings;
            applySettings();
        }
        return payload?.settings || null;
    };

    const activePreferenceTarget = () =>
        settingsForm.querySelector('[name="scope"]')?.value || "work";

    const scheduleBookSave = changedKey => {
        const snapshot = { ...state };
        const scope = activePreferenceTarget();
        saveQueue = saveQueue
            .then(async () => {
                const saved = await postSettings(
                    scope,
                    changedKey,
                    snapshot,
                    false);

                if (scope === "work" || scope === "book") {
                    state.hasBookOverride = true;
                }
                // Apply the saved values, except for settings that changed again
                // while this save was in flight (an appearance card sets several
                // keys at once; each key's own save carries its newer value).
                if (saved && state[changedKey] === snapshot[changedKey]) {
                    const merged = { ...state };
                    for (const [key, value] of Object.entries(saved)) {
                        if (state[key] === snapshot[key]) merged[key] = value;
                    }
                    state = merged;
                    applySettings();
                }
            })
            .catch(error => {
                showToast(error.message);
            });
    };

    const fontStacks = {
        "system-serif": 'Georgia, "Times New Roman", "Noto Serif JP", serif',
        "system-sans": 'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans JP", sans-serif',
        "literary-serif": '"Literata", Georgia, "Noto Serif JP", serif',
        "book-serif": '"Lora", Georgia, "Noto Serif JP", serif',
        "atkinson": '"Atkinson Hyperlegible", system-ui, "Noto Sans JP", sans-serif',
        "noto-serif-jp": '"Noto Serif JP", "Yu Mincho", serif',
        "noto-sans-jp": '"Noto Sans JP", system-ui, sans-serif'
    };

    const loadGoogleFont = family => {
        const normalized = (family || "").trim();
        if (!normalized || normalized.length > 80) return;
        const id = "reader-google-font";
        let link = document.getElementById(id);
        if (!link) {
            link = document.createElement("link");
            link.id = id;
            link.rel = "stylesheet";
            document.head.append(link);
        }
        const query = normalized.replace(/ /g, "+");
        link.href =
            "https://fonts.googleapis.com/css2?family=" +
            encodeURIComponent(query).replace(/%2B/g, "+") +
            ":wght@400;500;600;700&display=swap";
    };

    const bundledGoogleFonts = {
        "literary-serif": "Literata",
        "book-serif": "Lora",
        "atkinson": "Atkinson Hyperlegible",
        "noto-serif-jp": "Noto Serif JP",
        "noto-sans-jp": "Noto Sans JP"
    };

    const fontCss = font => {
        if (font?.startsWith("google:")) {
            const family = font.slice("google:".length).trim();
            loadGoogleFont(family);
            return `"${family.replace(/"/g, "")}", "Noto Serif JP", serif`;
        }
        const remoteFamily = bundledGoogleFonts[font];
        if (remoteFamily) loadGoogleFont(remoteFamily);
        return fontStacks[font] || fontStacks["literary-serif"];
    };

    const ensureFontOption = font => {
        const select = settingsForm.querySelector('[data-setting-key="fontFamily"]');
        if (!select || !font) return;
        if (!Array.from(select.options).some(option => option.value === font)) {
            const option = document.createElement("option");
            option.value = font;
            option.textContent = font.startsWith("google:")
                ? font.slice("google:".length)
                : font;
            select.append(option);
        }
    };

    const formatOutput = (key, value) => {
        if (key === "autoScrollSpeed") return `${Math.round(value)} px/s`;
        if (key === "fontSizeRem") return `${Number(value).toFixed(2)} rem`;
        if (key === "lineHeight") return Number(value).toFixed(2);
        if (key === "paragraphSpacingEm") return `${Number(value).toFixed(1)} em`;
        if (key === "textWidthPx") return `${Math.round(value)} px`;
        if (key === "backgroundIntensity") return `${Math.round(Number(value) * 100)}%`;
        if (key === "themeBlurPx") return `${Number(value).toFixed(1)} px`;
        if ([
            "themeEffectStrength",
            "themeBrightness",
            "themeContrast",
            "themeSaturation",
            "themeVignetteStrength",
            "themeGrainStrength",
            "themeTextBackdropStrength",
            "themeParallaxStrength",
            "themeTintStrength"
        ].includes(key)) {
            return `${Math.round(Number(value) * 100)}%`;
        }
        return String(value ?? "");
    };

    const syncControls = () => {
        ensureFontOption(state.fontFamily);
        settingsForm.querySelectorAll("[data-reader-setting]").forEach(control => {
            const key = control.dataset.settingKey;
            if (!(key in state)) return;
            if (control.type === "checkbox") {
                control.checked = Boolean(state[key]);
            } else {
                control.value = String(state[key] ?? "");
            }
        });

        settingsForm.querySelectorAll("[data-setting-output]").forEach(output => {
            const key = output.dataset.settingOutput;
            output.textContent = formatOutput(key, state[key]);
        });

        if (overrideState) {
            overrideState.textContent = state.hasBookOverride
                ? t("overrideWork", "Customized for this work")
                : state.hasGenreOverride
                    ? t("overrideGenre", "Genre default")
                    : state.hasTypeOverride
                        ? t("overrideType", "Type default")
                        : t("overrideMine", "My default");
        }
    };

    // A reading anchor is a paragraph plus a character offset into its text
    // (textContent, the same unit novel-position.js stores for Scroll mode). In
    // Pages mode a long paragraph can continue from the previous page, so the
    // offset marks the first character on the current page.
    const initialAnchor = () => {
        const language = shell.dataset.anchorLanguage || "ja";
        const index = shell.dataset.anchorParagraph;
        if (index == null || index === "") return null;
        const paragraph = shell.querySelector(
            `[data-reader-paragraph][data-language="${cssEscape(language)}"][data-index="${cssEscape(index)}"]`);
        return paragraph
            ? { paragraph, offset: Math.max(0, Number(shell.dataset.anchorOffset) || 0) }
            : null;
    };

    // Maps a textContent offset to a one-character range inside the paragraph.
    const characterRange = (paragraph, offset) => {
        const walker = document.createTreeWalker(paragraph, NodeFilter.SHOW_TEXT);
        let remaining = offset;
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const length = node.data.length;
            if (remaining < length) {
                const range = document.createRange();
                range.setStart(node, remaining);
                range.setEnd(node, remaining + 1);
                return range;
            }
            remaining -= length;
        }
        return null;
    };

    // Left edge of the character at (or the first rendered one after) offset.
    const characterLeft = (paragraph, offset, length) => {
        for (let index = offset; index < Math.min(length, offset + 8); index++) {
            const rect = characterRange(paragraph, index)?.getClientRects()[0];
            if (rect && (rect.width > 0 || rect.height > 0)) return rect.left;
        }
        return null;
    };

    // Page index of a viewport x coordinate. Capturing and restoring an anchor
    // both use this, so a saved anchor always maps back to the page it came from.
    const pageAt = left => {
        const width = Math.max(1, content.clientWidth);
        return Math.floor((content.scrollLeft + left - content.getBoundingClientRect().left + 1) / width);
    };

    const pagedAnchor = paragraphs => {
        const page = Math.round(content.scrollLeft / Math.max(1, content.clientWidth));
        const paragraph = paragraphs.find(item =>
            Array.from(item.getClientRects()).some(rect => rect.width > 0 && pageAt(rect.left) >= page));
        if (!paragraph) return { paragraph: paragraphs[0], offset: 0 };

        const first = paragraph.getClientRects()[0];
        if (!first || pageAt(first.left) >= page) return { paragraph, offset: 0 };

        // The paragraph started on an earlier page: find the first character laid
        // out on this page. Characters run in reading order across the columns,
        // so "on this page or later" is monotonic in the offset.
        const length = paragraph.textContent?.length || 0;
        let low = 0;
        let high = length;
        while (low < high) {
            const middle = Math.floor((low + high) / 2);
            const left = characterLeft(paragraph, middle, length);
            if (left !== null && pageAt(left) >= page) high = middle;
            else low = middle + 1;
        }
        return { paragraph, offset: Math.min(low, Math.max(0, length - 1)) };
    };

    const captureLogicalAnchor = () => {
        const language =
            shell.dataset.view === "de" && shell.dataset.hasTranslation === "true"
                ? "de"
                : "ja";
        const paragraphs = Array.from(shell.querySelectorAll(
            `[data-reader-paragraph][data-language="${language}"]`));

        if (paragraphs.length === 0) return null;

        if ((shell.dataset.readingMode || state.readingMode) === "paged") {
            return pagedAnchor(paragraphs);
        }

        const target = window.innerHeight * .28;
        let selected = paragraphs[0];
        for (const paragraph of paragraphs) {
            const rect = paragraph.getBoundingClientRect();
            if (rect.top <= target) selected = paragraph;
            if (rect.top <= target && rect.bottom >= target) break;
            if (rect.top > target) break;
        }
        const rect = selected.getBoundingClientRect();
        const length = selected.textContent?.length || 0;
        const fraction = rect.height <= 0 ? 0 : clamp((target - rect.top) / rect.height, 0, 1);
        return { paragraph: selected, offset: Math.round(length * fraction) };
    };

    // Page that shows the anchor's character (the paragraph start for offset 0).
    const pageOfAnchor = anchor => {
        const length = anchor.paragraph.textContent?.length || 0;
        const left = anchor.offset > 0 && anchor.offset < length
            ? characterLeft(anchor.paragraph, anchor.offset, length)
            : null;
        const edge = left ?? anchor.paragraph.getClientRects()[0]?.left;
        return edge == null ? currentPage : pageAt(edge);
    };

    const updateReadingProgress = () => {
        if (state.readingMode !== "paged") return;
        const progress =
            pageCount <= 1 ? 1000 : Math.round(currentPage / (pageCount - 1) * 1000);
        document.querySelectorAll("[data-reading-progress]").forEach(bar => {
            bar.style.width = (progress / 10) + "%";
        });
        shell.querySelectorAll("[data-reader-rail-fill]").forEach(bar => {
            bar.style.height = (progress / 10) + "%";
        });
        shell.querySelectorAll("[data-reader-percent]").forEach(output => {
            output.textContent = Math.round(progress / 10) + "%";
        });
    };

    // Runs once a page turn or swipe has settled.
    const sendPagedProgress = () => {
        if (state.readingMode !== "paged") return;
        const anchor = captureLogicalAnchor();
        settledPagedAnchor = anchor;
        if (!progressForm) return;
        const language =
            shell.dataset.view === "de" && shell.dataset.hasTranslation === "true"
                ? "de"
                : "ja";
        const progress =
            pageCount <= 1 ? 1000 : Math.round(currentPage / (pageCount - 1) * 1000);
        const data = new FormData(progressForm);
        setFormValue(data, "positionPermille", progress);
        setFormValue(data, "anchorLanguage", language);
        setFormValue(data, "anchorParagraphIndex", anchor?.paragraph.dataset.index ?? "");
        setFormValue(data, "anchorOffset", anchor?.offset ?? 0);

        fetch(progressForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" },
            keepalive: true
        }).catch(() => {});
    };

    // Reader frame slider/position text (reader-shell.js) for both modes.
    const emitLocation = () => {
        if (!frame) return;
        let page;
        let total;
        let value;
        let max;
        if (state.readingMode === "paged") {
            page = currentPage + 1;
            total = pageCount;
            value = currentPage;
            max = pageCount - 1;
        } else {
            const viewport = Math.max(1, window.innerHeight);
            const scrollable = Math.max(1, document.documentElement.scrollHeight - viewport);
            total = Math.max(1, Math.ceil(document.documentElement.scrollHeight / viewport));
            page = Math.min(total, Math.floor(window.scrollY / viewport) + 1);
            value = Math.round(clamp(window.scrollY / scrollable, 0, 1) * 1000);
            max = 1000;
        }
        const percent = state.readingMode === "paged"
            ? Math.round(page / total * 100)
            : Math.round(value / 10);
        const label = t("position", "{page} / {total}", { page, total });
        const valueText = t(
            "positionAria",
            "Page {page} of {total}, {percent}% of the chapter",
            { page, total, percent });
        shell.dispatchEvent(new CustomEvent("jularr:reader-location", {
            detail: { value, max, text: label, valueText }
        }));
        if (pageNumberOverlay) {
            pageNumberOverlay.textContent = state.readingMode === "paged" ? String(page) : "";
        }
    };

    const syncPageState = () => {
        if (state.readingMode !== "paged") return;
        const width = Math.max(1, content.clientWidth);
        // Columns advance by exactly one page width (novels.css: column gap = twice the
        // inline padding). The 2 px allowance absorbs sub-pixel rounding, which would
        // otherwise add an empty page after the last one.
        pageCount = Math.max(1, Math.ceil((content.scrollWidth - 2) / width));
        currentPage = clamp(Math.round(content.scrollLeft / width), 0, pageCount - 1);
        if (pageNumber) pageNumber.textContent = `${currentPage + 1} / ${pageCount}`;
        emitLocation();
        shell.querySelector("[data-reader-page-prev]")?.toggleAttribute("disabled", currentPage <= 0);
        shell.querySelector("[data-reader-page-next]")?.toggleAttribute("disabled", currentPage >= pageCount - 1);
        updateReadingProgress();
        renderPageBookmarks();
    };

    const animatePage = direction => {
        if (reduceMotion.matches || state.pageTransition === "none") return;
        const animation = `reader-turn-${state.pageTransition}-${direction}`;
        content.classList.remove(
            "reader-turn-curl-next", "reader-turn-curl-prev",
            "reader-turn-slide-next", "reader-turn-slide-prev",
            "reader-turn-fade-next", "reader-turn-fade-prev");
        void content.offsetWidth;
        content.classList.add(animation);
        setTimeout(() => content.classList.remove(animation), 430);
    };

    const goToPage = (page, animate = true) => {
        if (state.readingMode !== "paged") return;
        const next = clamp(page, 0, pageCount - 1);
        const direction = next >= currentPage ? "next" : "prev";
        if (animate && next !== currentPage) animatePage(direction);
        content.scrollTo({
            left: next * content.clientWidth,
            behavior: reduceMotion.matches ? "auto" : "smooth"
        });
        currentPage = next;
        syncPageState();
        window.setTimeout(() => {
            syncPageState();
            sendPagedProgress();
        }, reduceMotion.matches ? 0 : 360);
    };

    const setupPaged = anchor => {
        stopAutoScroll();
        shell.classList.toggle("reader-frame-fixed", frame);
        if (pageControls) pageControls.hidden = false;
        document.body.classList.add("novel-paged-body");
        settledPagedAnchor = anchor;

        requestAnimationFrame(() => {
            syncPageState();
            if (anchor) goToPage(pageOfAnchor(anchor), false);
            syncPageState();
        });

        // Web fonts and illustrations can finish after the first layout and change how
        // many columns the chapter needs; count again then (the page stays where it is).
        document.fonts?.ready.then(syncPageState).catch(() => {});
    };

    content.addEventListener("load", event => {
        if (event.target instanceof HTMLImageElement) syncPageState();
    }, true);

    const teardownPaged = anchor => {
        settledPagedAnchor = null;
        content.scrollLeft = 0;
        shell.classList.remove("reader-frame-fixed");
        if (pageControls) pageControls.hidden = true;
        document.body.classList.remove("novel-paged-body");

        requestAnimationFrame(() => {
            if (!anchor) return;
            const rect = anchor.paragraph.getBoundingClientRect();
            const length = anchor.paragraph.textContent?.length || 0;
            const fraction = length <= 0 ? 0 : clamp(anchor.offset / length, 0, 1);
            const top = window.scrollY + rect.top + rect.height * fraction - window.innerHeight * .28;
            window.scrollTo({ top: Math.max(0, top), behavior: "auto" });
        });
    };

    const applySettings = (preserveAnchor = true) => {
        const anchor = preserveAnchor ? captureLogicalAnchor() : initialAnchor();
        const previousMode = shell.dataset.readingMode || state.readingMode;

        shell.dataset.pageTransition = state.pageTransition;
        shell.dataset.twoPage = String(Boolean(state.twoPageSpread));
        shell.dataset.chapterStyle = state.chapterStyle;
        shell.dataset.paperStyle = state.paperStyle;
        shell.dataset.genreArtwork = String(Boolean(state.genreArtworkEnabled));
        shell.dataset.genreTheme = state.genreTheme || "auto";
        shell.dataset.textAlignment = state.textAlignment;
        shell.dataset.hyphenation = String(state.hyphenation !== false);
        shell.dataset.paragraphIndent = String(state.paragraphIndent !== false);
        shell.dataset.pageNumbers = String(state.showPageNumbers !== false);
        shell.dataset.illustrations = String(state.showIllustrations !== false);

        document.documentElement.style.setProperty("--novel-reader-size", `${state.fontSizeRem}rem`);
        document.documentElement.style.setProperty("--novel-reader-leading", state.lineHeight);
        document.documentElement.style.setProperty("--novel-reader-width", `${state.textWidthPx}px`);
        document.documentElement.style.setProperty("--reader-paragraph-spacing", `${state.paragraphSpacingEm}em`);
        document.documentElement.style.setProperty("--reader-background-intensity", state.backgroundIntensity);
        document.documentElement.style.setProperty("--reader-font-family", fontCss(state.fontFamily));

        shell.dispatchEvent(new CustomEvent("jularr:reader-settings", {
            detail: { settings: state }
        }));

        const bookmarkStyle = bookmarkForm?.querySelector('[name="style"]');
        const bookmarkColor = bookmarkForm?.querySelector('[name="color"]');
        if (bookmarkStyle) bookmarkStyle.value = state.bookmarkStyle;
        if (bookmarkColor) bookmarkColor.value = state.bookmarkColor;

        if (state.readingMode === "paged") {
            setupPaged(anchor);
        } else if (previousMode === "paged") {
            teardownPaged(anchor);
        } else {
            if (pageControls) pageControls.hidden = true;
            shell.classList.remove("reader-frame-fixed");
            document.body.classList.remove("novel-paged-body");
            requestAnimationFrame(emitLocation);
        }

        syncControls();
        decorateBookmarks();
    };

    const stopAutoScroll = () => {
        autoScrollRunning = false;
        autoScrollLastTime = null;
        if (autoScrollFrame != null) {
            cancelAnimationFrame(autoScrollFrame);
            autoScrollFrame = null;
        }
        if (autoScrollButton) {
            autoScrollButton.setAttribute("aria-pressed", "false");
        }
    };

    const autoScrollTick = time => {
        if (!autoScrollRunning || state.readingMode !== "continuous") {
            stopAutoScroll();
            return;
        }
        if (autoScrollLastTime == null) autoScrollLastTime = time;
        const elapsed = Math.min(80, time - autoScrollLastTime);
        autoScrollLastTime = time;
        const amount = Number(state.autoScrollSpeed || 36) * elapsed / 1000;
        window.scrollBy(0, amount);

        const max = document.documentElement.scrollHeight - window.innerHeight;
        if (window.scrollY >= max - 2) {
            stopAutoScroll();
            if (state.autoContinueChapters) openAdjacentChapter("next");
            return;
        }
        autoScrollFrame = requestAnimationFrame(autoScrollTick);
    };

    const toggleAutoScroll = () => {
        if (autoScrollRunning) {
            stopAutoScroll();
            return;
        }
        if (state.readingMode !== "continuous") {
            showToast(t("autoScrollScrollOnly", "Auto-scroll works in Scroll mode."));
            return;
        }
        autoScrollRunning = true;
        if (autoScrollButton) {
            autoScrollButton.setAttribute("aria-pressed", "true");
        }
        autoScrollFrame = requestAnimationFrame(autoScrollTick);
    };

    const collectBookmarks = () => {
        shell.querySelectorAll("[data-bookmark-marker]").forEach(marker => {
            const id = marker.dataset.bookmarkId;
            if (!id) return;
            const rawPosition = marker.style.top || marker.style.left || "";
            const percent = Number.parseFloat(rawPosition);
            const existing = bookmarkState.get(id) || {
                id,
                style: state.bookmarkStyle,
                color: state.bookmarkColor
            };
            if (Number.isFinite(percent) && !(existing.positionPermille > 0)) {
                existing.positionPermille = Math.round(percent * 10);
            }
            bookmarkState.set(id, existing);
        });

        shell.querySelectorAll("[data-saved-bookmark]").forEach(element => {
            const id = element.dataset.bookmarkId;
            if (!id) return;
            bookmarkState.set(id, {
                id,
                positionPermille: Number(element.dataset.position || 0),
                style: element.dataset.bookmarkStyle || state.bookmarkStyle,
                color: element.dataset.bookmarkColor || state.bookmarkColor
            });
        });

        shell.querySelectorAll("[data-bookmark-card]").forEach(card => {
            const id = card.dataset.bookmarkId;
            if (!id || card.dataset.bookmarkChapterId !== shell.dataset.chapterId) return;
            const existing = bookmarkState.get(id);
            if (!existing) return;
            existing.style = card.dataset.bookmarkStyle || existing.style || state.bookmarkStyle;
            existing.color = card.dataset.bookmarkColor || existing.color || state.bookmarkColor;
        });
    };

    const decorateBookmarks = () => {
        collectBookmarks();

        shell.querySelectorAll("[data-bookmark-card]").forEach(card => {
            const item = bookmarkState.get(card.dataset.bookmarkId);
            const style = item?.style || card.dataset.bookmarkStyle || state.bookmarkStyle;
            const color = item?.color || card.dataset.bookmarkColor || state.bookmarkColor;
            card.dataset.bookmarkStyle = style;
            card.dataset.bookmarkColor = color;
            card.style.setProperty("--bookmark-color", color);

            let appearance = card.querySelector(".novel-bookmark-appearance");
            if (!appearance) {
                appearance = document.createElement("div");
                appearance.className = "novel-bookmark-appearance";
                appearance.innerHTML =
                    '<select data-bookmark-style-control aria-label="Lesezeichen-Stil">' +
                    '<option value="fabric">Stoff</option>' +
                    '<option value="paper">Papier</option>' +
                    '<option value="leather">Leder</option>' +
                    '<option value="cord">Schnur</option>' +
                    '<option value="minimal">Minimal</option>' +
                    '</select>' +
                    '<input type="color" data-bookmark-color-control aria-label="Lesezeichen-Farbe" />';
                const remove = card.querySelector("[data-remove-bookmark-form], [data-remove-bookmark-button]");
                if (remove) card.insertBefore(appearance, remove);
                else card.append(appearance);
            }

            const styleControl = card.querySelector("[data-bookmark-style-control]");
            const colorControl = card.querySelector("[data-bookmark-color-control]");
            if (styleControl) styleControl.value = style;
            if (colorControl) colorControl.value = color;
        });

        const bookmarkCount =
            shell.querySelectorAll("[data-bookmark-card]").length;
        const currentCount = bookmarkState.size;
        const noteCount =
            bookmarkCount + shell.querySelectorAll("[data-highlight-card]").length;
        const currentBadge = shell.querySelector("[data-current-bookmark-count]");
        const notesBadge = shell.querySelector("[data-reader-note-count]");
        const bookmarkButton = shell.querySelector("[data-reader-bookmark]");
        if (currentBadge) {
            currentBadge.textContent = String(currentCount);
            currentBadge.hidden = currentCount <= 0;
        }
        if (notesBadge) {
            notesBadge.textContent = String(noteCount);
            notesBadge.hidden = noteCount <= 0;
        }
        bookmarkButton?.classList.toggle("has-bookmarks", currentCount > 0);

        shell.querySelectorAll("[data-bookmark-marker]").forEach(marker => {
            const item = bookmarkState.get(marker.dataset.bookmarkId);
            if (!item) return;
            marker.dataset.bookmarkStyle = item.style;
            marker.style.setProperty("--bookmark-color", item.color);
        });

        renderPageBookmarks();
    };

    const renderPageBookmarks = () => {
        if (!pageBookmarks) return;
        pageBookmarks.replaceChildren();
        if (state.readingMode !== "paged") return;

        for (const bookmark of bookmarkState.values()) {
            const targetPage =
                pageCount <= 1
                    ? 0
                    : clamp(Math.round(bookmark.positionPermille / 1000 * (pageCount - 1)), 0, pageCount - 1);
            if (targetPage !== currentPage) continue;

            const button = document.createElement("button");
            button.type = "button";
            button.className = "novel-page-bookmark";
            button.dataset.bookmarkStyle = bookmark.style || state.bookmarkStyle;
            button.style.setProperty("--bookmark-color", bookmark.color || state.bookmarkColor);
            button.dataset.pageBookmark = bookmark.id;
            button.title = t("bookmark", "Bookmark");
            button.setAttribute("aria-label", t("bookmarkOnPage", "Bookmark on this page"));
            pageBookmarks.append(button);
        }
    };

    const addPagedBookmarkUi = bookmark => {
        if (!bookmark?.id) return;
        const item = {
            id: bookmark.id,
            positionPermille: Number(bookmark.positionPermille || 0),
            style: bookmark.style || state.bookmarkStyle,
            color: bookmark.color || state.bookmarkColor
        };
        bookmarkState.set(item.id, item);

        const hidden = document.createElement("span");
        hidden.hidden = true;
        hidden.dataset.savedBookmark = "";
        hidden.dataset.bookmarkId = item.id;
        hidden.dataset.chapterId = bookmark.chapterId || shell.dataset.chapterId;
        hidden.dataset.position = String(item.positionPermille);
        hidden.dataset.language = bookmark.language || "ja";
        hidden.dataset.paragraph =
            bookmark.paragraphIndex == null ? "" : String(bookmark.paragraphIndex);
        hidden.dataset.offset = String(bookmark.characterOffset || 0);
        hidden.dataset.anchorText = bookmark.anchorText || "";
        hidden.dataset.bookmarkStyle = item.style;
        hidden.dataset.bookmarkColor = item.color;
        shell.append(hidden);

        const list = shell.querySelector("[data-bookmark-list]");
        if (list) {
            const card = document.createElement("article");
            card.className = "novel-note-card";
            card.dataset.bookmarkCard = "";
            card.dataset.bookmarkId = item.id;
            card.dataset.bookmarkChapterId = bookmark.chapterId || shell.dataset.chapterId;
            card.dataset.bookmarkStyle = item.style;
            card.dataset.bookmarkColor = item.color;

            const link = document.createElement("a");
            link.href =
                `/Novels/Read/${encodeURIComponent(bookmark.chapterId || shell.dataset.chapterId)}?bookmark=${encodeURIComponent(item.id)}`;
            link.dataset.localBookmarkId = item.id;

            const title = document.createElement("strong");
            title.textContent =
                bookmark.label ||
                t("bookmarkAt", "Bookmark · {position}", { position: Math.round(item.positionPermille / 10) + "%" });
            link.append(title);
            if (bookmark.anchorText) {
                const excerpt = document.createElement("span");
                excerpt.textContent = bookmark.anchorText;
                link.append(excerpt);
            }

            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "novel-note-remove";
            remove.dataset.removeBookmarkButton = "";
            remove.dataset.bookmarkId = item.id;
            remove.textContent = t("remove", "Remove");
            card.append(link, remove);
            list.prepend(card);
        }

        shell.querySelectorAll("[data-bookmark-track]").forEach(track => {
            const marker = document.createElement("button");
            marker.type = "button";
            marker.className = "novel-bookmark-marker";
            marker.dataset.bookmarkMarker = "";
            marker.dataset.bookmarkId = item.id;
            marker.dataset.bookmarkStyle = item.style;
            marker.style.setProperty("--bookmark-color", item.color);
            const percent = clamp(item.positionPermille / 10, 1.5, 98.5);
            if (track.dataset.orientation === "horizontal") marker.style.left = percent + "%";
            else marker.style.top = percent + "%";
            marker.innerHTML = "<span></span>";
            marker.title = bookmark.anchorText || t("bookmark", "Bookmark");
            track.append(marker);
        });

        shell.querySelector("[data-empty-bookmarks]")?.classList.add("is-hidden");
        decorateBookmarks();
    };

    const savePagedBookmark = async () => {
        if (!bookmarkForm) return;
        const anchor = captureLogicalAnchor();
        const language =
            shell.dataset.view === "de" && shell.dataset.hasTranslation === "true"
                ? "de"
                : "ja";
        const position =
            pageCount <= 1 ? 1000 : Math.round(currentPage / (pageCount - 1) * 1000);
        const data = new FormData(bookmarkForm);
        setFormValue(data, "positionPermille", position);
        setFormValue(data, "language", language);
        setFormValue(data, "paragraphIndex", anchor?.paragraph.dataset.index ?? "");
        setFormValue(data, "characterOffset", anchor?.offset ?? 0);
        setFormValue(data, "label", "");
        setFormValue(data, "style", state.bookmarkStyle);
        setFormValue(data, "color", state.bookmarkColor);

        const response = await fetch(bookmarkForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) {
            throw new Error(t("bookmarkSaveFailed", "The bookmark could not be saved"));
        }

        addPagedBookmarkUi(await response.json());
        showToast(t("bookmarkSaved", "Bookmark saved"));
    };

    const saveBookmarkAppearance = async (id, style, color) => {
        if (!bookmarkAppearanceForm) return;
        const data = new FormData(bookmarkAppearanceForm);
        setFormValue(data, "bookmarkId", id);
        setFormValue(data, "style", style);
        setFormValue(data, "color", color);

        const response = await fetch(bookmarkAppearanceForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) throw new Error(t("bookmarkChangeFailed", "The bookmark could not be changed"));
        const payload = await response.json();
        const bookmark = bookmarkState.get(id);
        if (bookmark) {
            bookmark.style = payload.style || style;
            bookmark.color = payload.color || color;
        }
        decorateBookmarks();
    };

    settingsForm.addEventListener("input", event => {
        const control = event.target.closest("[data-reader-setting]");
        if (!control) return;
        const key = control.dataset.settingKey;
        state[key] = control.type === "checkbox"
            ? control.checked
            : control.type === "range"
                ? Number(control.value)
                : control.value;

        if (key === "genreTheme") {
            state.resolvedGenreTheme = state.genreTheme;
        }
        applySettings();
    });

    settingsForm.addEventListener("change", event => {
        const control = event.target.closest("[data-reader-setting]");
        if (!control) return;
        scheduleBookSave(control.dataset.settingKey);
    });

    settingsForm.addEventListener("submit", event => event.preventDefault());

    shell.querySelector("[data-reader-save-defaults]")?.addEventListener("click", async () => {
        try {
            await postSettings("default", null);
            showToast(t("defaultsSaved", "Saved as your default."));
        } catch (error) {
            showToast(error.message);
        }
    });

    shell.querySelector("[data-reader-reset-book]")?.addEventListener("click", async () => {
        if (!resetForm) return;
        try {
            const response = await fetch(resetForm.action, {
                method: "POST",
                body: new FormData(resetForm),
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) throw new Error(t("resetFailed", "The settings could not be reset."));
            const payload = await response.json();
            if (payload?.settings) {
                state = payload.settings;
                applySettings();
            }
            showToast(t("resetDone", "This work uses your default again."));
        } catch (error) {
            showToast(error.message);
        }
    });

    shell.querySelector("[data-reader-apply-google-font]")?.addEventListener("click", () => {
        const input = shell.querySelector("[data-reader-google-font]");
        const family = input?.value?.trim();
        if (!family) return;
        state.fontFamily = `google:${family}`;
        ensureFontOption(state.fontFamily);
        applySettings();
        scheduleBookSave("fontFamily");
    });

    autoScrollButton?.addEventListener("click", toggleAutoScroll);
    wakeLockButton?.addEventListener("click", () => void toggleReaderWakeLock());
    immersiveButton?.addEventListener("click", () => void toggleImmersiveReader());

    document.addEventListener("fullscreenchange", syncImmersiveButton);
    document.addEventListener("webkitfullscreenchange", syncImmersiveButton);
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            void acquireReaderWakeLock();
        } else {
            void releaseReaderWakeLock();
        }
    });
    window.addEventListener("pagehide", () => void releaseReaderWakeLock());

    content.addEventListener("pointerdown", event => {
        if (autoScrollRunning && !event.target.closest("a, button, input, select, textarea")) {
            stopAutoScroll();
        }
    }, { passive: true });

    settingsPanel?.addEventListener("toggle", () => {
        if (settingsPanel.open) stopAutoScroll();
    });

    shell.addEventListener("jularr:reader-settings-response", event => {
        if (!event.detail?.settings) return;
        state = { ...state, ...event.detail.settings };
        applySettings();
    });

    const chapterLink = which => shell.querySelector(`a[data-novel-chapter-link="${which}"]`);

    const openAdjacentChapter = which => {
        const link = chapterLink(which);
        if (!link) return false;
        link.click();
        return true;
    };

    shell.addEventListener("jularr:reader-page-edge", event => {
        const direction = Number(event.detail?.direction || 0);
        if (!direction) return;
        if (state.readingMode !== "paged") {
            // Frame page buttons in Scroll mode move by one screen.
            window.scrollBy({
                top: direction * window.innerHeight * .85,
                behavior: reduceMotion.matches ? "auto" : "smooth"
            });
            return;
        }
        const speaking = shell.dataset.readerTts && shell.dataset.readerTts !== "idle";
        const next = currentPage + direction;
        // Turning past either end opens the adjacent chapter; read-aloud page
        // following never changes chapters.
        if (!speaking && next >= pageCount && openAdjacentChapter("next")) return;
        if (!speaking && next < 0 && openAdjacentChapter("previous")) return;
        goToPage(next);
    });

    shell.addEventListener("jularr:reader-seek", event => {
        const value = Number(event.detail?.value || 0);
        if (state.readingMode === "paged") {
            goToPage(value, false);
            return;
        }
        const max = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
        window.scrollTo({ top: max * value / 1000, behavior: "auto" });
    });

    // Search hits and note jumps inside the current chapter (novel-search.js).
    shell.addEventListener("jularr:novel-jump-paragraph", event => {
        const { language, index } = event.detail || {};
        const target = shell.querySelector(
            `[data-reader-paragraph][data-language="${cssEscape(language || "ja")}"][data-index="${cssEscape(String(index))}"]`);
        if (!target) return;
        if (state.readingMode === "paged") {
            goToPage(pageOfAnchor({ paragraph: target, offset: 0 }), false);
        } else {
            const top = window.scrollY + target.getBoundingClientRect().top - window.innerHeight * .28;
            window.scrollTo({ top: Math.max(0, top), behavior: reduceMotion.matches ? "auto" : "smooth" });
        }
        target.classList.remove("novel-paragraph-flash");
        void target.offsetWidth;
        target.classList.add("novel-paragraph-flash");
    });

    shell.addEventListener("jularr:reader-timer-end", stopAutoScroll);

    // Scroll mode: with "continue automatically" on, scrolling on at the very end
    // of the chapter opens the next one.
    let endIntent = 0;
    const atChapterEnd = () =>
        window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4;
    const continueAtEnd = direction => {
        if (state.readingMode === "paged" || !state.autoContinueChapters || direction <= 0) return;
        if (!atChapterEnd()) {
            endIntent = 0;
            return;
        }
        endIntent += 1;
        if (endIntent >= 3) {
            endIntent = 0;
            openAdjacentChapter("next");
        }
    };
    window.addEventListener("wheel", event => continueAtEnd(Math.sign(event.deltaY)), { passive: true });
    window.addEventListener("keydown", event => {
        if (["PageDown", "ArrowDown", " "].includes(event.key)) continueAtEnd(1);
    });
    window.addEventListener("scroll", () => {
        if (state.readingMode === "paged") return;
        window.clearTimeout(emitLocation.timer);
        emitLocation.timer = window.setTimeout(emitLocation, 60);
    }, { passive: true });

    content.addEventListener("scroll", () => {
        if (state.readingMode !== "paged") return;
        window.clearTimeout(content.__readerPageTimer);
        content.__readerPageTimer = window.setTimeout(() => {
            syncPageState();
            sendPagedProgress();
        }, 180);
    }, { passive: true });

    document.addEventListener("click", event => {
        if (state.readingMode !== "paged") return;

        const bookmarkButton = event.target.closest("[data-reader-bookmark]");
        if (bookmarkButton) {
            event.preventDefault();
            event.stopImmediatePropagation();
            savePagedBookmark().catch(error => showToast(error.message));
            return;
        }

        const localBookmark = event.target.closest("[data-local-bookmark-id]");
        if (localBookmark) {
            const item = bookmarkState.get(localBookmark.dataset.localBookmarkId);
            if (!item) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            const target =
                pageCount <= 1
                    ? 0
                    : Math.round(item.positionPermille / 1000 * (pageCount - 1));
            goToPage(target);
        }
    }, true);

    document.addEventListener("change", event => {
        const styleControl = event.target.closest("[data-bookmark-style-control]");
        const colorControl = event.target.closest("[data-bookmark-color-control]");
        const control = styleControl || colorControl;
        if (!control) return;
        const card = control.closest("[data-bookmark-card]");
        const id = card?.dataset.bookmarkId;
        if (!id) return;

        const currentItem = bookmarkState.get(id);
        const style =
            card.querySelector("[data-bookmark-style-control]")?.value ||
            currentItem?.style ||
            card.dataset.bookmarkStyle ||
            state.bookmarkStyle;
        const color =
            card.querySelector("[data-bookmark-color-control]")?.value ||
            currentItem?.color ||
            card.dataset.bookmarkColor ||
            state.bookmarkColor;

        card.dataset.bookmarkStyle = style;
        card.dataset.bookmarkColor = color;
        card.style.setProperty("--bookmark-color", color);
        if (currentItem) {
            currentItem.style = style;
            currentItem.color = color;
        }
        decorateBookmarks();
        saveBookmarkAppearance(id, style, color).catch(error => showToast(error.message));
    });

    pageBookmarks?.addEventListener("click", event => {
        const button = event.target.closest("[data-page-bookmark]");
        if (!button) return;
        const item = bookmarkState.get(button.dataset.pageBookmark);
        if (!item) return;
        const target =
            pageCount <= 1 ? 0 : Math.round(item.positionPermille / 1000 * (pageCount - 1));
        goToPage(target);
    });

    window.addEventListener("resize", () => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => {
            if (state.readingMode === "paged") {
                // The columns have already reflowed by now, so the anchor comes from
                // the last settled page rather than from the resized layout.
                const anchor = settledPagedAnchor || captureLogicalAnchor();
                requestAnimationFrame(() => {
                    syncPageState();
                    if (anchor) goToPage(pageOfAnchor(anchor), false);
                });
            }
        }, 160);
    });

    const bookmarkObserver = new MutationObserver(() => decorateBookmarks());
    const bookmarkList = shell.querySelector("[data-bookmark-list]");
    if (bookmarkList) bookmarkObserver.observe(bookmarkList, { childList: true, subtree: true });
    shell.querySelectorAll("[data-bookmark-track]").forEach(track =>
        bookmarkObserver.observe(track, { childList: true, subtree: true }));

    keepAwake = readKeepAwakePreference();
    syncWakeLockButton();
    syncImmersiveButton();
    collectBookmarks();
    syncControls();
    applySettings(false);
    void acquireReaderWakeLock();
})();