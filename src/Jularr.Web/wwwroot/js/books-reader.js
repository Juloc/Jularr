(async () => {
    "use strict";

    // Books reader (reader frame, see docs/UNIFIED_READER.md "Reader frame").
    // reader-shell.js owns the shared chrome (menus, contents panel, settings
    // sheet, progress slider, fullscreen, timer, share); this adapter owns the
    // book: pagination into a paper spread, scroll mode, language views,
    // bookmarks, highlights/notes, in-book search and chapter navigation.
    // PDF books render their pages through books-reader-pdf.js in the same
    // frame; `pdf` is that page adapter, null for EPUB chapters.

    const root = document.querySelector("[data-book-reader]");
    if (!root) return;

    const scriptUrl = document.currentScript?.src;
    if (!scriptUrl) return;
    const sourceScriptUrl = new URL(scriptUrl);
    const reflowModuleUrl = new URL("reflow-reader.js", sourceScriptUrl);
    const buildVersion = sourceScriptUrl.searchParams.get("v");
    if (buildVersion) reflowModuleUrl.searchParams.set("v", buildVersion);
    const {
        clamp,
        permilleForIndex,
        indexForPermille,
        scrollPermille: reflowScrollPermille,
        scrollTopForPermille,
        captureContinuousAnchor,
        capturePagedRectAnchor,
        createReflowTextRenderer,
        measurePagedSequence,
        viewIndexForDisplayPage
    } = await import(reflowModuleUrl.href);

    const readJson = (selector, fallback) => {
        try {
            return JSON.parse(root.querySelector(selector)?.textContent || "") ?? fallback;
        } catch {
            return fallback;
        }
    };

    const text = Object.assign(
        {},
        readJson("[data-reader-frame-text]", {}),
        readJson("[data-book-reader-text]", {}));
    const t = (key, fallback, values = {}) => {
        let value = text[key] || fallback;
        for (const [name, replacement] of Object.entries(values)) {
            value = value.replaceAll("{" + name + "}", String(replacement));
        }
        return value;
    };

    const original = root.querySelector("[data-book-original]");
    const translated = root.querySelector("[data-book-translated]");
    const stage = root.querySelector(".book-stage");
    const spread = root.querySelector("[data-book-spread]");
    const flow = root.querySelector("[data-book-flow]");
    const columns = root.querySelector("[data-book-columns]");
    const turnShade = root.querySelector("[data-book-turn-shade]");
    const translateForm = root.querySelector("[data-book-translate-form]");
    const settingsForm = root.querySelector("[data-book-settings-form]");
    const toastElement = root.querySelector("[data-book-toast]");
    const pageNumbers = {
        left: root.querySelector('[data-book-page-number="left"]'),
        right: root.querySelector('[data-book-page-number="right"]')
    };
    const bookmarkButton = root.querySelector("[data-bookmark-button]");
    const autoScrollButton = root.querySelector("[data-book-autoscroll-toggle]");
    const targetLanguage = root.dataset.targetLanguage || "id";
    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const compactQuery = window.matchMedia("(max-width: 720px)");
    const pdfContainer = root.querySelector("[data-book-pdf]");
    let pdf = null;

    // #374: Book reading progress and bookmarks always go through the same
    // offline-first sync queue the Novel reader uses (offline-library-repository.js's
    // forWork(workId).queueProgress/queueBookmarkUpsert/queueBookmarkRemove,
    // wrapping manager.queueSyncEvent) — online and offline alike, one canonical
    // write path. See docs/OFFLINE_LIBRARY.md.
    const workId = root.dataset.workId || "";
    const repository = workId && window.JularrOfflineLibraryRepository
        ? window.JularrOfflineLibraryRepository.forWork(workId)
        : null;

    let settings = Object.assign({
        readingMode: "paged",
        pageTransition: "curl",
        twoPageSpread: true,
        autoScrollSpeed: 36,
        fontFamily: "literary-serif",
        fontSizeRem: 1.06,
        lineHeight: 1.9,
        paragraphSpacingEm: 0.85,
        textWidthPx: 760,
        textAlignment: "start",
        chapterStyle: "classic",
        paperStyle: "cream",
        genreArtworkEnabled: true,
        genreTheme: "auto",
        backgroundAssetId: "auto",
        backgroundIntensity: 0.055,
        backgroundMotionMode: "auto",
        themeEffectStrength: 1,
        themeBrightness: 1,
        themeContrast: 1,
        themeSaturation: 1,
        themeBlurPx: 0,
        themeVignetteStrength: 1,
        themeGrainStrength: 1,
        themeTextBackdropStrength: 1,
        themeParallaxStrength: 1,
        themeTintStrength: 1,
        bookmarkStyle: "fabric",
        bookmarkColor: "#b04455",
        hasBookOverride: false
    }, readJson("[data-book-settings-json]", {}));

    const fontStacks = {
        "system-serif": 'Georgia, "Times New Roman", serif',
        "system-sans": 'system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
        "literary-serif": '"Literata", Georgia, "Times New Roman", serif',
        "book-serif": '"Lora", Georgia, "Times New Roman", serif',
        "atkinson": '"Atkinson Hyperlegible", system-ui, sans-serif',
        "noto-serif-jp": '"Noto Serif JP", "Yu Mincho", serif',
        "noto-sans-jp": '"Noto Sans JP", system-ui, sans-serif'
    };

    const numericSettings = new Set([
        "autoScrollSpeed", "fontSizeRem", "lineHeight", "paragraphSpacingEm",
        "textWidthPx", "backgroundIntensity", "themeEffectStrength", "themeBrightness",
        "themeContrast", "themeSaturation", "themeBlurPx", "themeVignetteStrength",
        "themeGrainStrength", "themeTextBackdropStrength", "themeParallaxStrength",
        "themeTintStrength"
    ]);

    let view = root.dataset.view || "original";
    let restoring = true;
    let reflowRenderer = null;

    const toast = message => {
        if (!toastElement || !message) return;
        toastElement.textContent = message;
        toastElement.hidden = false;
        window.clearTimeout(toast.timer);
        toast.timer = window.setTimeout(() => {
            toastElement.hidden = true;
        }, 2400);
    };

    // The status lets the shell tell a revoked permission or missing content from a network failure.
    const httpError = async response => Object.assign(new Error(await response.text()), { status: response.status });

    const failed = error => {
        console.warn(error);
        if (root.readerShell) root.readerShell.reportFailure(error);
        else toast(t("books.read.actionFailed", "That did not work. Please try again."));
    };

    // ---- Language views ---------------------------------------------------------

    const activeColumn = () => {
        if (view === "translated" && translated && !translated.hidden) return translated;
        return original;
    };

    const paragraphsOf = column =>
        column ? Array.from(column.querySelectorAll("p[data-book-paragraph]")) : [];

    const currentAnchorLanguage = () => view === "original" ? "original" : targetLanguage;

    const syncViewControls = () => {
        root.querySelectorAll("[data-reader-view]").forEach(button => {
            button.setAttribute("aria-checked", button.dataset.readerView === view ? "true" : "false");
        });
        // Chapter links keep the chosen language view.
        root.querySelectorAll("a[data-book-chapter-link]").forEach(link => {
            const url = new URL(link.href, window.location.origin);
            if (view === "original") url.searchParams.delete("view");
            else url.searchParams.set("view", view);
            link.href = url.pathname + url.search;
        });
    };

    const setView = next => {
        if (pdfContainer) {
            setPdfView(next);
            return;
        }
        if ((next === "translated" || next === "both") && root.dataset.hasTranslation !== "true") return;
        if (next === view && !restoring) return;
        stopAutoScroll();
        const anchor = captureAnchor();
        view = next;
        root.dataset.view = next;
        if (original) original.hidden = next === "translated";
        if (translated) translated.hidden = next === "original";
        syncViewControls();
        scheduleLayout(anchor);
    };

    // ---- Settings -----------------------------------------------------------------

    const syncQuickControls = () => {
        root.querySelectorAll("[data-book-mode]").forEach(button => {
            button.setAttribute(
                "aria-pressed",
                button.dataset.bookMode === settings.readingMode ? "true" : "false");
        });
        root.querySelectorAll("[data-book-paper]").forEach(button => {
            button.setAttribute(
                "aria-checked",
                button.dataset.bookPaper === settings.paperStyle ? "true" : "false");
        });
    };

    const formatSetting = (key, value) => {
        if (key === "autoScrollSpeed") return Math.round(Number(value)) + " px/s";
        if (key === "fontSizeRem") return Number(value).toFixed(2) + " rem";
        if (key === "lineHeight") return Number(value).toFixed(2);
        if (key === "paragraphSpacingEm") return Number(value).toFixed(1) + " em";
        if (key === "textWidthPx") return Math.round(Number(value)) + " px";
        if (key === "backgroundIntensity") return Math.round(Number(value) * 100) + "%";
        if (key === "themeBlurPx") return Number(value).toFixed(1) + " px";
        if (numericSettings.has(key)) return Math.round(Number(value) * 100) + "%";
        return String(value == null ? "" : value);
    };

    const syncSettingControls = () => {
        if (!settingsForm) return;
        settingsForm.querySelectorAll("[data-book-setting]").forEach(control => {
            const key = control.dataset.bookSetting;
            if (!(key in settings)) return;
            if (control.type === "checkbox") control.checked = Boolean(settings[key]);
            else control.value = String(settings[key]);
        });
        settingsForm.querySelectorAll("[data-book-setting-output]").forEach(output => {
            const key = output.dataset.bookSettingOutput;
            output.textContent = formatSetting(key, settings[key]);
        });
    };

    const applySettings = () => {
        const anchor = pdfContainer ? null : relayoutAnchor();
        root.dataset.paperStyle = settings.paperStyle;
        root.dataset.chapterStyle = settings.chapterStyle;
        root.dataset.pageTransition = settings.pageTransition;
        root.dataset.twoPageSpread = settings.twoPageSpread ? "true" : "false";

        root.style.setProperty("--book-reader-font-size", Number(settings.fontSizeRem) + "rem");
        root.style.setProperty("--book-reader-line-height", String(settings.lineHeight));
        root.style.setProperty("--book-reader-paragraph-spacing", Number(settings.paragraphSpacingEm) + "em");
        root.style.setProperty("--book-reader-text-width", Math.round(Number(settings.textWidthPx)) + "px");
        root.style.setProperty("--book-reader-font", fontStacks[settings.fontFamily] || fontStacks["literary-serif"]);
        root.style.setProperty("--book-reader-align", settings.textAlignment === "justify" ? "justify" : "start");
        root.style.setProperty("--book-bookmark-color", settings.bookmarkColor || "#b04455");

        syncSettingControls();
        syncQuickControls();

        root.dispatchEvent(new CustomEvent("jularr:reader-settings", {
            detail: { settings }
        }));

        if (settings.readingMode !== "continuous") stopAutoScroll();
        if (pdfContainer) {
            pdf?.apply(settings);
            return;
        }
        scheduleLayout(anchor);
    };

    let settingsSave = Promise.resolve();

    const saveSettings = async (scope, changedKey) => {
        if (!settingsForm) return;
        const data = new FormData(settingsForm);
        const names = {
            readingMode: "ReadingMode", pageTransition: "PageTransition",
            twoPageSpread: "TwoPageSpread", autoScrollSpeed: "AutoScrollSpeed",
            fontFamily: "FontFamily", fontSizeRem: "FontSizeRem", lineHeight: "LineHeight",
            paragraphSpacingEm: "ParagraphSpacingEm", textWidthPx: "TextWidthPx",
            textAlignment: "TextAlignment", chapterStyle: "ChapterStyle",
            paperStyle: "PaperStyle", genreArtworkEnabled: "GenreArtworkEnabled",
            genreTheme: "GenreTheme", backgroundAssetId: "BackgroundAssetId",
            backgroundIntensity: "BackgroundIntensity", backgroundMotionMode: "BackgroundMotionMode",
            themeEffectStrength: "ThemeEffectStrength", themeBrightness: "ThemeBrightness",
            themeContrast: "ThemeContrast", themeSaturation: "ThemeSaturation",
            themeBlurPx: "ThemeBlurPx", themeVignetteStrength: "ThemeVignetteStrength",
            themeGrainStrength: "ThemeGrainStrength",
            themeTextBackdropStrength: "ThemeTextBackdropStrength",
            themeParallaxStrength: "ThemeParallaxStrength", themeTintStrength: "ThemeTintStrength",
            bookmarkStyle: "BookmarkStyle", bookmarkColor: "BookmarkColor"
        };
        for (const [key, name] of Object.entries(names)) data.set(name, String(settings[key]));
        data.set("scope", scope);
        data.set("changedKey", changedKey || "");

        const response = await fetch(settingsForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) throw await httpError(response);
        const result = await response.json();
        if (result?.settings) applyServerSettings(result.settings);
    };

    // Edits not yet confirmed by their own save win over older server
    // responses, so two quick changes (mode, then paper) never undo each other.
    const unsavedEdits = new Map();
    function applyServerSettings(next) {
        settings = Object.assign(settings, next);
        for (const [key, value] of unsavedEdits) settings[key] = value;
        applySettings();
    }

    const scheduleSettingSave = changedKey => {
        const scope = settingsForm?.querySelector('[name="scope"]')?.value || "work";
        if (scope === "work") settings.hasBookOverride = true;
        const value = settings[changedKey];
        unsavedEdits.set(changedKey, value);
        settingsSave = settingsSave
            .then(() => saveSettings(scope, changedKey))
            .catch(failed)
            .finally(() => {
                if (unsavedEdits.get(changedKey) === value) unsavedEdits.delete(changedKey);
            });
    };

    settingsForm?.querySelectorAll("[data-book-setting]").forEach(control => {
        const eventName = control.type === "range" ? "input" : "change";
        control.addEventListener(eventName, () => {
            const key = control.dataset.bookSetting;
            let value = control.type === "checkbox" ? control.checked : control.value;
            if (numericSettings.has(key)) value = Number(value);
            settings[key] = value;
            applySettings();
            window.clearTimeout(control._bookSaveTimer);
            control._bookSaveTimer = window.setTimeout(
                () => scheduleSettingSave(key),
                control.type === "range" ? 300 : 0);
        });
    });

    // Quick controls change the canonical settings controls, so they persist
    // through the same ReaderPreferences path as the settings sheet.
    const setSettingControl = (key, value) => {
        const control = settingsForm?.querySelector(`[data-book-setting="${key}"]`);
        if (!control) return;
        if (control.type === "checkbox") control.checked = Boolean(value);
        else control.value = String(value);
        control.dispatchEvent(new Event("change", { bubbles: true }));
    };

    root.querySelectorAll("[data-book-mode]").forEach(button => {
        button.addEventListener("click", () => setSettingControl("readingMode", button.dataset.bookMode));
    });
    root.querySelectorAll("[data-book-paper]").forEach(button => {
        button.addEventListener("click", () => setSettingControl("paperStyle", button.dataset.bookPaper));
    });

    root.addEventListener("jularr:reader-settings-response", event => {
        if (!event.detail?.settings) return;
        applyServerSettings(event.detail.settings);
    });

    // ---- Layout and pagination ---------------------------------------------------

    const layout = {
        paged: false,
        pages: 1,
        stride: 1,
        columnStride: 1,
        pageCount: 1,
        viewCount: 1
    };
    let currentView = 0;
    let layoutFrame = 0;
    let pendingAnchor = null;
    let turnTimer = 0;

    const isPaged = () => settings.readingMode === "paged" && view !== "both";

    const topBarBottom = () =>
        root.querySelector("[data-reader-chrome-primary]")?.getBoundingClientRect().bottom || 0;

    const scrollPermille = () =>
        reflowScrollPermille(
            window.scrollY,
            document.documentElement.scrollHeight,
            window.innerHeight);

    const currentPermille = () =>
        layout.paged
            ? permilleForIndex(currentView, layout.viewCount)
            : scrollPermille();

    const viewForPermille = permille =>
        indexForPermille(permille, layout.viewCount);

    // One canonical anchor algorithm is shared with Novel/LN. Books supplies
    // its paper-spread geometry, while the reflow runtime owns anchor semantics.
    function captureAnchor() {
        const list = paragraphsOf(activeColumn());
        if (!list.length) return null;

        if (layout.paged) {
            const origin = columns.getBoundingClientRect().left;
            const viewOfRect = rect => {
                const page = Math.floor(
                    (rect.left - origin + 2) / Math.max(1, layout.columnStride));
                return Math.floor(page / Math.max(1, layout.pages));
            };
            const anchor = capturePagedRectAnchor(
                list,
                currentView,
                viewOfRect);
            return anchor
                ? { index: anchor.index, part: anchor.part || 0 }
                : null;
        }

        const anchor = captureContinuousAnchor(
            list,
            topBarBottom() + 8);
        return anchor ? { index: anchor.index } : null;
    }

    const paragraphAt = index =>
        activeColumn()?.querySelector(`p[data-book-paragraph="${Number(index)}"]`) || null;

    // Measured against the column box itself, so a running page-turn
    // transform cannot skew the result.
    const viewForParagraph = (paragraph, part = 0) => {
        const rects = paragraph ? paragraph.getClientRects() : [];
        const rect = rects[Math.min(part, rects.length - 1)];
        if (!rect) return 0;
        const x = rect.left - columns.getBoundingClientRect().left;
        const page = Math.floor((x + 2) / layout.columnStride);
        return Math.max(0, Math.min(layout.viewCount - 1, Math.floor(page / layout.pages)));
    };

    const flash = paragraph => {
        if (!paragraph || reduceMotion.matches) return;
        paragraph.classList.remove("book-paragraph-flash");
        void paragraph.offsetWidth;
        paragraph.classList.add("book-paragraph-flash");
    };

    function scheduleLayout(anchor) {
        if (anchor && !pendingAnchor) pendingAnchor = anchor;
        cancelAnimationFrame(layoutFrame);
        layoutFrame = requestAnimationFrame(layoutNow);
    }

    function layoutNow() {
        const anchor = pendingAnchor;
        pendingAnchor = null;
        const paged = isPaged();
        layout.paged = paged;
        root.dataset.bookLayout = paged ? "paged" : "scroll";
        root.classList.toggle("reader-frame-fixed", paged);

        if (!paged) {
            reflowRenderer?.setMode("continuous");
            reflowRenderer?.setPageState(0, 1);
            columns.style.transform = "";
            spread.style.cssText = "";
            layout.pages = 1;
            layout.viewCount = 1;
            layout.pageCount = 1;
            currentView = 0;
            if (anchor) {
                const paragraph = paragraphAt(anchor.index);
                if (paragraph) {
                    window.scrollTo({
                        top: window.scrollY + paragraph.getBoundingClientRect().top - topBarBottom() - 16,
                        behavior: "auto"
                    });
                    if (anchor.flash) flash(paragraph);
                }
            } else if (anchor === null && restoring) {
                const initial = Number(root.dataset.progress || "0");
                window.scrollTo({
                    top: scrollTopForPermille(
                        initial,
                        document.documentElement.scrollHeight,
                        window.innerHeight),
                    behavior: "auto"
                });
            }
            renderPageNumbers();
            emitLocation();
            finishRestore();
            return;
        }

        window.clearTimeout(turnTimer);
        columns.classList.remove("is-turning", "is-fading");
        const styles = getComputedStyle(stage);
        const width = stage.clientWidth - parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight);
        const height = stage.clientHeight - parseFloat(styles.paddingTop) - parseFloat(styles.paddingBottom);
        const compact = compactQuery.matches;
        const pages = settings.twoPageSpread !== false && width >= 860 && height >= 420 ? 2 : 1;
        const sheetHeight = Math.max(280, Math.floor(height));
        const provisionalPad = compact ? 22 : 48;
        const sheetWidth = Math.max(240, Math.floor(pages === 2
            ? Math.min(width / 2, sheetHeight * 0.74)
            : Math.min(width, compact ? width : Number(settings.textWidthPx) + provisionalPad * 2)));
        const padX = compact ? 22 : clamp(Math.round(sheetWidth * 0.09), 28, 60);
        const padTop = compact ? 28 : clamp(Math.round(sheetHeight * 0.08), 36, 64);
        const padBottom = compact ? 44 : clamp(Math.round(sheetHeight * 0.09), 50, 74);
        const artWidth = Math.round(Math.min(sheetWidth * 0.46, 230));

        spread.style.setProperty("--pages", String(pages));
        spread.style.setProperty("--sheet-w", sheetWidth + "px");
        spread.style.setProperty("--sheet-h", sheetHeight + "px");
        spread.style.setProperty("--pad-x", padX + "px");
        spread.style.setProperty("--pad-top", padTop + "px");
        spread.style.setProperty("--pad-bottom", padBottom + "px");
        spread.style.setProperty("--art-w", artWidth + "px");
        spread.style.setProperty("--art-h", Math.round(artWidth * 1.5) + "px");
        root.dataset.pages = String(pages);

        const columnWidth = sheetWidth - padX * 2;
        const gap = padX * 2;
        layout.pages = pages;
        layout.columnStride = columnWidth + gap;
        layout.stride = sheetWidth * pages;

        columns.style.transform = "translate3d(0,0,0)";
        const contentWidth = columns.scrollWidth;
        const measured = measurePagedSequence({
            scrollWidth: contentWidth,
            columnStride: layout.columnStride,
            pagesPerView: pages,
            extraExtent: gap,
            rounding: "round"
        });
        layout.pageCount = measured.pageCount;
        layout.viewCount = measured.viewCount;
        reflowRenderer?.setMode("paged");
        reflowRenderer?.setPageState(currentView, layout.viewCount);

        let target;
        currentView = 0;
        if (anchor) {
            target = viewForParagraph(paragraphAt(anchor.index), anchor.part || 0);
        } else {
            target = viewForPermille(Number(root.dataset.progress || "0"));
        }
        goToView(target, { animate: false, save: false });
        if (anchor?.flash) flash(paragraphAt(anchor.index));
        finishRestore();
    }

    const renderPageNumbers = () => {
        if (!layout.paged) {
            if (pageNumbers.left) pageNumbers.left.textContent = "";
            if (pageNumbers.right) pageNumbers.right.textContent = "";
            return;
        }
        const first = currentView * layout.pages + 1;
        if (pageNumbers.left) pageNumbers.left.textContent = String(first);
        if (pageNumbers.right) {
            pageNumbers.right.textContent = layout.pages === 2 && first + 1 <= layout.pageCount
                ? String(first + 1)
                : "";
        }
    };

    // Book edge (#446): the visible thickness of the read/remaining page stacks
    // approximates reading progress, from a thin left stack at the start to a
    // thin right stack at the end. Purely decorative (aria-hidden); the CSS
    // custom properties are the only thing this touches.
    const STACK_MIN_PX = 3;
    const STACK_MAX_PX = 16;
    const updateStackDepth = () => {
        if (!spread) return;
        if (!layout.paged) {
            spread.style.removeProperty("--book-stack-left");
            spread.style.removeProperty("--book-stack-right");
            return;
        }
        const max = compactQuery.matches ? 6 : STACK_MAX_PX;
        const ratio = layout.viewCount > 1 ? currentView / (layout.viewCount - 1) : 0;
        spread.style.setProperty("--book-stack-left", Math.round(STACK_MIN_PX + ratio * (max - STACK_MIN_PX)) + "px");
        spread.style.setProperty("--book-stack-right", Math.round(STACK_MIN_PX + (1 - ratio) * (max - STACK_MIN_PX)) + "px");
    };

    // The slider and the label show one fraction: in Pages mode the slider runs
    // from 0 to the page count and its value is the last page on screen, the same
    // page the percentage is computed from; in Scroll mode both use the scroll
    // position.
    const dispatchLocation = ({ first, last, total, value, max, percent }) => {
        const label = t("reader.frame.position", "{page} / {total} ({percent}%)", {
            page: first === last ? first : first + "–" + last,
            total,
            percent
        });
        root.dispatchEvent(new CustomEvent("jularr:reader-location", {
            detail: { value, max, text: label, valueText: label }
        }));
        syncBookmarkButton();
    };

    function emitLocation() {
        if (layout.paged) {
            const first = Math.min(layout.pageCount, currentView * layout.pages + 1);
            const last = Math.min(layout.pageCount, first + layout.pages - 1);
            dispatchLocation({
                first,
                last,
                total: layout.pageCount,
                value: last,
                max: layout.pageCount,
                percent: Math.round(last / layout.pageCount * 100)
            });
            return;
        }
        const viewport = Math.max(1, window.innerHeight);
        const total = Math.max(1, Math.ceil(document.documentElement.scrollHeight / viewport));
        const page = Math.min(total, Math.floor(window.scrollY / viewport) + 1);
        const position = scrollPermille();
        dispatchLocation({
            first: page,
            last: page,
            total,
            value: position,
            max: 1000,
            percent: Math.round(position / 10)
        });
    }

    // A slider value is a page number (Pages mode) or a permille (Scroll mode).
    const viewForSliderPage = value =>
        viewIndexForDisplayPage(value, layout.pages);

    function goToView(target, { animate = true, save = true } = {}) {
        const next = clamp(target, 0, layout.viewCount - 1);
        const direction = Math.sign(next - currentView);
        currentView = next;
        reflowRenderer?.setPageState(currentView, layout.viewCount);
        const transition = reduceMotion.matches ? "none" : settings.pageTransition;
        const animated = animate && direction !== 0 && transition !== "none";

        window.clearTimeout(turnTimer);
        columns.classList.remove("is-turning", "is-fading");
        if (animated && transition === "fade") {
            columns.classList.add("is-fading");
        } else if (animated) {
            columns.classList.add("is-turning");
        }
        columns.style.transform = `translate3d(${-next * layout.stride}px,0,0)`;
        if (animated) {
            turnTimer = window.setTimeout(() => {
                columns.classList.remove("is-turning", "is-fading");
            }, 360);
        }

        renderPageNumbers();
        updateStackDepth();
        emitLocation();
        if (save) {
            readerMoved = true;
            queueProgressSave();
        }
    }

    const chapterLink = which => root.querySelector(`a[data-book-chapter-link="${which}"]`);

    const openChapter = (which, atEnd) => {
        const link = chapterLink(which);
        if (!link) return false;
        const url = new URL(link.href, window.location.origin);
        if (atEnd) url.searchParams.set("pos", "1000");
        // Offline navigation (offline-library-repository.js) intercepts link clicks.
        if (!navigator.onLine) {
            link.click();
            return true;
        }
        window.location.assign(url.pathname + url.search);
        return true;
    };

    reflowRenderer = createReflowTextRenderer({
        initialMode: isPaged() ? "paged" : "continuous",
        goToPage: page => goToView(page),
        turnContinuous: direction => {
            window.scrollBy({
                top: direction * window.innerHeight * 0.85,
                behavior: reduceMotion.matches ? "auto" : "smooth"
            });
        },
        onPageEdge: direction => {
            const speaking =
                root.dataset.readerTts && root.dataset.readerTts !== "idle";
            if (speaking) return;
            if (direction < 0) openChapter("previous", true);
            else openChapter("next", false);
        },
        getScrollPermille: scrollPermille,
        scrollToPermille: value => jumpToPermille(value),
        captureAnchor
    });

    // ---- Interactive page-turn drag (#446) -----------------------------------------
    // Holding and dragging a page follows the pointer instead of jumping straight to
    // the next/previous view; tap-zones, swipe and keyboard paging (reader-shell.js /
    // the keydown handler above) are untouched and still drive goToView() directly.
    // This drag feeds the exact same currentView/goToView() pagination: while
    // dragging it slides the real .book-columns transform live, and on release it
    // either commits with goToView(currentView ± 1) or springs back to the same
    // view with the .is-turning transition already used for every other page turn.
    // Disabled for reduced motion and for the "instant paging" choice (pageTransition
    // "none"), which both fall back to the existing quick tap/swipe handling.
    const DRAG_ACTIVATE_PX = 10;
    const DRAG_COMMIT_RATIO = 0.32;
    const DRAG_FLICK_PX_MS = 0.5;
    const dragInteractiveSelector = "a,button,input,textarea,select,summary,[contenteditable='true']";
    let dragState = null;

    const dragEnabled = () =>
        Boolean(spread) && !pdfContainer && layout.paged &&
        settings.pageTransition !== "none" && !reduceMotion.matches;

    const setTurnShade = (direction, progress) => {
        if (!turnShade) return;
        turnShade.dataset.turnDirection = direction;
        spread.style.setProperty("--book-turn-progress", progress.toFixed(3));
    };

    const settleDrag = (dx, velocity) => {
        columns.classList.remove("is-dragging");
        root.classList.remove("book-is-dragging");
        setTurnShade("", 0);
        const direction = dx < 0 ? 1 : dx > 0 ? -1 : 0;
        const distanceRatio = layout.stride ? Math.abs(dx) / layout.stride : 0;
        const flick = Math.abs(velocity) > DRAG_FLICK_PX_MS && Math.sign(velocity) === -direction;
        const atStart = currentView <= 0;
        const atEnd = currentView >= layout.viewCount - 1;
        const commit = direction !== 0 && !(direction === 1 && atEnd) && !(direction === -1 && atStart) &&
            (distanceRatio > DRAG_COMMIT_RATIO || flick);
        if (commit) {
            goToView(currentView + direction);
            return;
        }
        window.clearTimeout(turnTimer);
        columns.classList.add("is-turning");
        columns.style.transform = `translate3d(${-currentView * layout.stride}px,0,0)`;
        turnTimer = window.setTimeout(() => {
            columns.classList.remove("is-turning", "is-fading");
        }, 360);
    };

    if (spread) {
        spread.addEventListener("pointerdown", event => {
            if (event.pointerType === "mouse" && event.button !== 0) return;
            if (event.target instanceof HTMLElement && event.target.closest(dragInteractiveSelector)) return;
            dragState = {
                id: event.pointerId,
                active: false,
                startX: event.clientX,
                startY: event.clientY,
                samples: [{ x: event.clientX, t: performance.now() }]
            };
        });

        spread.addEventListener("pointermove", event => {
            if (!dragState || dragState.id !== event.pointerId) return;
            const dx = event.clientX - dragState.startX;
            const dy = event.clientY - dragState.startY;
            if (!dragState.active) {
                if (Math.abs(dx) < DRAG_ACTIVATE_PX || Math.abs(dx) < Math.abs(dy)) return;
                if (!dragEnabled()) {
                    dragState = null;
                    return;
                }
                const selection = window.getSelection();
                if (selection && !selection.isCollapsed && selection.toString().trim()) {
                    dragState = null;
                    return;
                }
                dragState.active = true;
                dragState.atStart = currentView <= 0;
                dragState.atEnd = currentView >= layout.viewCount - 1;
                window.getSelection()?.removeAllRanges();
                window.clearTimeout(turnTimer);
                columns.classList.remove("is-turning", "is-fading");
                columns.classList.add("is-dragging");
                root.classList.add("book-is-dragging");
                spread.setPointerCapture(event.pointerId);
            }
            event.preventDefault();
            dragState.samples.push({ x: event.clientX, t: performance.now() });
            if (dragState.samples.length > 6) dragState.samples.shift();
            const edge = (dx < 0 && dragState.atEnd) || (dx > 0 && dragState.atStart);
            const clampedDx = edge
                ? Math.sign(dx) * Math.min(Math.abs(dx), 70) * 0.4
                : Math.max(-layout.stride, Math.min(layout.stride, dx));
            columns.style.transform = `translate3d(${-currentView * layout.stride + clampedDx}px,0,0)`;
            const progress = layout.stride ? Math.min(1, Math.abs(clampedDx) / layout.stride) : 0;
            setTurnShade(clampedDx < 0 ? "next" : "prev", edge ? progress * 0.5 : progress);
        });

        spread.addEventListener("pointerup", event => {
            if (!dragState || dragState.id !== event.pointerId) return;
            const wasActive = dragState.active;
            if (wasActive) {
                event.stopPropagation();
                const dx = event.clientX - dragState.startX;
                const now = performance.now();
                const early = dragState.samples.find(sample => now - sample.t <= 120) || dragState.samples[0];
                const dt = Math.max(1, now - early.t);
                const velocity = (event.clientX - early.x) / dt;
                settleDrag(dx, velocity);
            }
            dragState = null;
        });

        spread.addEventListener("pointercancel", event => {
            if (dragState?.id === event.pointerId && dragState.active) {
                event.stopPropagation();
                settleDrag(0, 0);
            }
            dragState = null;
        });
    }

    const jumpToParagraph = index => {
        const paragraph = paragraphAt(index);
        if (!paragraph) return;
        if (layout.paged) {
            goToView(viewForParagraph(paragraph), { animate: false });
        } else {
            window.scrollTo({
                top: window.scrollY + paragraph.getBoundingClientRect().top - topBarBottom() - 16,
                behavior: reduceMotion.matches ? "auto" : "smooth"
            });
        }
        flash(paragraph);
    };

    const jumpToPermille = permille => {
        if (layout.paged) {
            goToView(viewForPermille(permille), { animate: false });
            return;
        }
        window.scrollTo({
            top: scrollTopForPermille(
                permille,
                document.documentElement.scrollHeight,
                window.innerHeight),
            behavior: "auto"
        });
    };

    function finishRestore() {
        if (!restoring) return;
        restoring = false;
        requestAnimationFrame(() => {
            root.classList.remove("reader-chrome-hidden");
            root.dispatchEvent(new CustomEvent("jularr:reader-restoring", {
                detail: { active: false }
            }));
        });
    }

    root.addEventListener("jularr:reader-page-edge", event => {
        const direction = Number(event.detail?.direction || 0);
        if (!direction) return;
        if (pdfContainer) {
            pdf?.turn(direction);
            return;
        }

        reflowRenderer?.setMode(layout.paged ? "paged" : "continuous");
        reflowRenderer?.setPageState(currentView, layout.viewCount);
        reflowRenderer?.turn(direction);
    });

    root.addEventListener("jularr:reader-seek", event => {
        const value = Number(event.detail?.value || 0);
        if (pdfContainer) {
            pdf?.seek(value);
            return;
        }

        reflowRenderer?.setMode(layout.paged ? "paged" : "continuous");
        reflowRenderer?.setPageState(currentView, layout.viewCount);
        if (layout.paged) reflowRenderer?.seekPage(viewForSliderPage(value));
        else reflowRenderer?.seekPermille(value);
    });

    // After a drag the slider snaps to the page actually shown.
    root.querySelector("[data-reader-progress-slider]")?.addEventListener("change", () => {
        requestAnimationFrame(() => {
            if (pdfContainer) pdf?.refresh();
            else emitLocation();
        });
    });

    // Keep the paragraph on screen when fonts load, the window resizes or the
    // contents panel opens; the initial restore owns the first layout.
    // Repeated relayouts without reader movement (panel toggles, resizes) keep
    // one anchor, so the position does not drift a page at a time.
    let stickyAnchor = null;
    let readerMoved = true;
    function relayoutAnchor() {
        if (restoring) return null;
        if (readerMoved || !stickyAnchor) stickyAnchor = captureAnchor();
        readerMoved = false;
        return stickyAnchor;
    }
    const relayout = () => scheduleLayout(relayoutAnchor());
    if (pdfContainer) {
        // The page adapter watches its own stage size.
        root.addEventListener("jularr:reader-layout", () => pdf?.relayout());
    } else {
        new ResizeObserver(relayout).observe(stage);
        root.addEventListener("jularr:reader-layout", relayout);
        document.fonts?.ready?.then(relayout);
    }

    let scrollFrame = 0;
    window.addEventListener("scroll", () => {
        if (pdfContainer || layout.paged) return;
        if (scrollFrame) return;
        scrollFrame = requestAnimationFrame(() => {
            scrollFrame = 0;
            readerMoved = true;
            emitLocation();
            if (!restoring) queueProgressSave();
        });
    }, { passive: true });

    // PDF zoom is renderer-specific. Generic page navigation keys are owned by
    // reader-shell.js and arrive here through jularr:reader-page-edge / seek.
    document.addEventListener("keydown", event => {
        if (!pdfContainer || !pdf || event.defaultPrevented ||
            event.ctrlKey || event.metaKey || event.altKey) {
            return;
        }

        const target = event.target;
        if (target instanceof HTMLElement &&
            (target.matches("input, textarea, select") || target.isContentEditable ||
             target.closest("[data-reader-menu],[data-reader-contents],[data-reader-settings-container],[role='dialog']"))) {
            return;
        }

        if (event.key === "+" || event.key === "=") {
            event.preventDefault();
            pdf.zoom(1);
        } else if (event.key === "-") {
            event.preventDefault();
            pdf.zoom(-1);
        }
    });

    // ---- Progress -----------------------------------------------------------------

    // A PDF position is the page's chapter (one chapter per page); an EPUB
    // position is the place in the open chapter.
    const currentProgress = () => pdf
        ? pdf.currentPosition()
        : { chapterId: root.dataset.chapterId, positionPermille: currentPermille() };

    let saveTimer = 0;
    function queueProgressSave() {
        if (restoring) return;
        const { chapterId, positionPermille } = currentProgress();
        if (!chapterId) return;
        root.dataset.progress = String(positionPermille);
        window.clearTimeout(saveTimer);
        saveTimer = window.setTimeout(async () => {
            // Reloading or sharing a PDF book opens the page on screen.
            if (pdf) history.replaceState(history.state, "", readerUrl(chapterId));
            if (!repository) return;
            try {
                await repository.queueProgress({
                    chapterId,
                    positionPermille,
                    anchorLanguage: currentAnchorLanguage()
                });
            } catch {
            }
        }, 900);
    }

    // ---- Auto-scroll (Scroll mode) --------------------------------------------------

    let autoScrollFrame = 0;
    let autoScrollLast = 0;
    let autoScrollRunning = false;

    function stopAutoScroll() {
        if (autoScrollFrame) cancelAnimationFrame(autoScrollFrame);
        autoScrollFrame = 0;
        autoScrollLast = 0;
        autoScrollRunning = false;
        autoScrollButton?.setAttribute("aria-pressed", "false");
    }

    const autoScrollTick = time => {
        if (!autoScrollRunning) return;
        if (!autoScrollLast) autoScrollLast = time;
        const delta = Math.min(60, time - autoScrollLast);
        autoScrollLast = time;
        window.scrollBy(0, Number(settings.autoScrollSpeed) * delta / 1000);
        if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2) {
            stopAutoScroll();
            return;
        }
        autoScrollFrame = requestAnimationFrame(autoScrollTick);
    };

    autoScrollButton?.addEventListener("click", () => {
        if (settings.readingMode !== "continuous") return;
        if (autoScrollRunning) {
            stopAutoScroll();
            return;
        }
        autoScrollRunning = true;
        autoScrollButton.setAttribute("aria-pressed", "true");
        autoScrollFrame = requestAnimationFrame(autoScrollTick);
    });

    root.addEventListener("jularr:reader-timer-end", stopAutoScroll);
    document.addEventListener("selectionchange", () => {
        if (autoScrollRunning && !window.getSelection()?.isCollapsed) stopAutoScroll();
    });

    // ---- Translation --------------------------------------------------------------

    let pollTimer = 0;

    const renderTranslation = paragraphs => {
        if (!translated) return;
        translated.replaceChildren(...(paragraphs || []).map((value, index) => {
            const paragraph = document.createElement("p");
            paragraph.dataset.bookParagraph = String(index);
            paragraph.textContent = value;
            return paragraph;
        }));
        root.dataset.hasTranslation = "true";
        root.querySelectorAll('[data-reader-view="translated"], [data-reader-view="both"]')
            .forEach(button => {
                button.hidden = false;
                button.disabled = false;
            });
        translateForm?.remove();
        setView("translated");
        toast(t("books.read.translationReady", "Translation ready."));
    };

    const pollTranslation = async () => {
        window.clearTimeout(pollTimer);
        try {
            const url = new URL(window.location.href);
            url.searchParams.set("handler", "TranslationStatus");
            url.searchParams.set("lang", targetLanguage);
            const response = await fetch(url, {
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (response.ok) {
                const result = await response.json();
                if (result.status === "ready") {
                    renderTranslation(result.paragraphs);
                    return;
                }
            } else if (response.status === 403) {
                return;
            }
        } catch {
        }
        pollTimer = window.setTimeout(pollTranslation, 2500);
    };

    translateForm?.addEventListener("submit", async event => {
        event.preventDefault();
        const button = translateForm.querySelector("button");
        if (button) {
            button.disabled = true;
            button.textContent = t("books.read.translationQueued", "Translation queued…");
        }
        try {
            const response = await fetch(translateForm.action, {
                method: "POST",
                body: new FormData(translateForm),
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) throw await httpError(response);
            void pollTranslation();
        } catch (error) {
            if (button) button.disabled = false;
            failed(error);
        }
    });

    root.querySelectorAll("[data-reader-view]").forEach(button => {
        button.addEventListener("click", () => setView(button.dataset.readerView));
    });

    // ---- PDF translation ------------------------------------------------------------
    // A PDF page is one chapter. The translated view shows the current page's translated
    // text over the page and offers to translate it when that page is still pending.

    const pdfTranslation = root.querySelector("[data-book-pdf-translation]");
    const pdfTranslateForm = root.querySelector("[data-book-pdf-translate-form]");

    const pdfTranslationUrl = (chapterId, handler) => {
        const url = new URL("/Books/Read/" + chapterId, window.location.origin);
        url.searchParams.set("handler", handler);
        url.searchParams.set("lang", targetLanguage);
        return url;
    };

    // Ready translated text covers the page; while a page is waiting, failed or not prepared the drawn
    // original stays readable and the panel collapses into a compact state bar.
    const pdfTranslationPollLimit = 24;
    let pdfTranslationPolls = 0;

    const showPdfTranslation = ({ paragraphs = [], message = "", canTranslate = false, retry = false }) => {
        const ready = paragraphs.length > 0;
        pdfTranslation.dataset.state = ready ? "ready" : "pending";
        pdfTranslation.querySelector("[data-book-pdf-translation-text]").replaceChildren(...paragraphs.map(value => {
            const paragraph = document.createElement("p");
            paragraph.textContent = value;
            return paragraph;
        }));
        pdfTranslation.querySelector("[data-book-pdf-translation-label]").hidden = !ready;
        const status = pdfTranslation.querySelector("[data-book-pdf-translation-status]");
        status.textContent = message;
        status.hidden = !message;
        pdfTranslateForm.hidden = !canTranslate && !retry;
        const button = pdfTranslateForm.querySelector("button");
        button.textContent = retry
            ? t("books.read.translationRetry", "Try again")
            : t("books.read.translateInto", "Translate into {language}").replace("{language}", button.dataset.languageName);
        button.disabled = false;
    };

    const waitForPdfTranslation = () => {
        pdfTranslationPolls += 1;
        if (pdfTranslationPolls > pdfTranslationPollLimit) {
            showPdfTranslation({ message: t("books.read.translationNotReady", "The translation is not ready yet."), retry: true });
            return;
        }
        showPdfTranslation({ message: t("books.read.translationQueued", "Translation queued…") });
        pollTimer = window.setTimeout(() => void loadPdfTranslation(true), 2500);
    };

    async function loadPdfTranslation(waiting = false) {
        window.clearTimeout(pollTimer);
        const chapterId = pdf?.currentPosition().chapterId;
        if (view !== "translated" || !chapterId) return;
        if (!waiting) pdfTranslationPolls = 0;
        try {
            const response = await fetch(pdfTranslationUrl(chapterId, "TranslationStatus"), {
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (chapterId !== pdf.currentPosition().chapterId || view !== "translated") return;
            if (response.status === 403) {
                showPdfTranslation({ message: t("books.read.pdfTranslationUnavailable", "Translation is not available for this book.") });
                return;
            }
            if (!response.ok) throw await httpError(response);
            const result = await response.json();
            if (result.status === "ready") {
                showPdfTranslation({ paragraphs: result.paragraphs });
                if (waiting) toast(t("books.read.translationReady", "Translation ready."));
                return;
            }
            if (waiting) {
                waitForPdfTranslation();
                return;
            }
            showPdfTranslation({ canTranslate: true });
        } catch (error) {
            console.warn(error);
            showPdfTranslation({ message: t("books.read.translationFailed", "The translation could not be loaded."), retry: true });
        }
    }

    function setPdfView(next) {
        view = next === "translated" ? "translated" : "original";
        root.dataset.view = view;
        if (pdfTranslation) pdfTranslation.hidden = view !== "translated";
        syncViewControls();
        void loadPdfTranslation();
    }

    pdfTranslateForm?.addEventListener("submit", async event => {
        event.preventDefault();
        const chapterId = pdf?.currentPosition().chapterId;
        if (!chapterId) return;
        const button = pdfTranslateForm.querySelector("button");
        button.disabled = true;
        try {
            const response = await fetch(pdfTranslationUrl(chapterId, "Translate"), {
                method: "POST",
                body: new FormData(pdfTranslateForm),
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) throw await httpError(response);
            pdfTranslationPolls = 0;
            waitForPdfTranslation();
        } catch (error) {
            console.warn(error);
            showPdfTranslation({ message: t("books.read.translationFailed", "The translation could not be loaded."), retry: true });
        }
    });

    // ---- Server helpers -------------------------------------------------------------

    const antiforgeryToken = () =>
        root.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const handlerUrl = (handler, extra) => {
        const url = new URL(window.location.href);
        for (const key of ["pos", "p", "view"]) url.searchParams.delete(key);
        url.searchParams.set("handler", handler);
        url.searchParams.set("lang", targetLanguage);
        for (const [key, value] of Object.entries(extra || {})) {
            if (value == null || value === "") url.searchParams.delete(key);
            else url.searchParams.set(key, String(value));
        }
        return url;
    };

    const getJson = async (handler, extra) => {
        const response = await fetch(handlerUrl(handler, extra), {
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) throw await httpError(response);
        return response.json();
    };

    const postHandler = async (handler, values) => {
        const data = new FormData();
        const token = antiforgeryToken();
        if (token) data.set("__RequestVerificationToken", token);
        data.set("lang", targetLanguage);
        for (const [key, value] of Object.entries(values || {})) {
            if (value != null) data.set(key, String(value));
        }
        const response = await fetch(handlerUrl(handler), {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) throw await httpError(response);
        const type = response.headers.get("content-type") || "";
        return type.includes("application/json") ? response.json() : null;
    };

    const prop = (value, name) =>
        value ? value[name] ?? value[name.charAt(0).toUpperCase() + name.slice(1)] : undefined;

    const currentChapterId = () => (root.dataset.chapterId || "").toLowerCase();

    const readerUrl = (chapterId, { position = null, paragraph = null, requestedView = view } = {}) => {
        const url = new URL("/Books/Read/" + chapterId, window.location.origin);
        url.searchParams.set("lang", targetLanguage);
        if (position != null) url.searchParams.set("pos", String(clamp(Math.round(position), 0, 1000)));
        if (paragraph != null) url.searchParams.set("p", String(paragraph));
        if (requestedView && requestedView !== "original") url.searchParams.set("view", requestedView);
        return url.pathname + url.search;
    };

    const viewForLanguage = language =>
        language === "original" ? "original" : "translated";

    const ensureViewForLanguage = language => {
        const wanted = viewForLanguage(language);
        if (wanted === "original" && view === "translated") setView("original");
        if (wanted === "translated" && view === "original") setView("translated");
    };

    // ---- Bookmarks ------------------------------------------------------------------

    // EPUB: the open chapter's bookmarks. PDF: every bookmark of the book, since
    // all pages are open at once (loaded with the annotations).
    const bookmarkItem = item => ({
        id: String(prop(item, "id")),
        chapterId: String(prop(item, "chapterId") || root.dataset.chapterId || ""),
        positionPermille: Number(prop(item, "positionPermille") || 0)
    });
    let chapterBookmarks = (readJson("[data-book-bookmarks-json]", []) || []).map(bookmarkItem);
    let allAnnotations = null;

    const pdfPageOf = item => pdf?.pageForPosition(prop(item, "chapterId"), Number(prop(item, "positionPermille") || 0));

    const bookmarkHere = () => {
        if (pdf) {
            const [first, last] = pdf.visiblePages();
            return chapterBookmarks.find(item => {
                const page = pdfPageOf(item);
                return page >= first && page <= last;
            }) || null;
        }
        if (layout.paged) {
            return chapterBookmarks.find(item => viewForPermille(item.positionPermille) === currentView) || null;
        }
        const position = scrollPermille();
        return chapterBookmarks.find(item => Math.abs(item.positionPermille - position) <= 12) || null;
    };

    function syncBookmarkButton() {
        const active = Boolean(bookmarkHere());
        const label = active
            ? t("books.read.removePageBookmark", "Remove bookmark")
            : t("books.read.bookmarkPage", "Bookmark this page");
        if (bookmarkButton) {
            bookmarkButton.setAttribute("aria-pressed", active ? "true" : "false");
            bookmarkButton.setAttribute("aria-label", label);
            bookmarkButton.title = label;
        }
    }

    async function toggleBookmark() {
        if (!repository) return;
        const existing = bookmarkHere();
        try {
            if (existing) {
                await repository.queueBookmarkRemove(existing.id, { chapterId: existing.chapterId });
                chapterBookmarks = chapterBookmarks.filter(item => item.id !== existing.id);
                if (allAnnotations) {
                    allAnnotations.bookmarks = (prop(allAnnotations, "bookmarks") || [])
                        .filter(item => String(prop(item, "id")) !== existing.id);
                }
                toast(t("books.read.bookmarkRemoved", "Bookmark removed."));
            } else {
                const { chapterId, positionPermille: position } = currentProgress();
                const created = await repository.queueBookmarkUpsert({
                    chapterId,
                    positionPermille: position,
                    language: currentAnchorLanguage(),
                    paragraphIndex: null,
                    characterOffset: 0,
                    anchorText: null,
                    label: null
                });
                chapterBookmarks.push({ id: String(created.id), chapterId, positionPermille: position });
                if (allAnnotations) {
                    allAnnotations.bookmarks = (prop(allAnnotations, "bookmarks") || []).concat([{
                        id: created.id,
                        chapterId,
                        chapterNumber: pdf
                            ? pdf.visiblePages()[0]
                            : Number(root.dataset.chapterNumber || 0),
                        chapterTitle: pdf ? "" : root.querySelector("[data-book-chapter-title]")?.textContent || "",
                        positionPermille: position,
                        language: currentAnchorLanguage()
                    }]);
                }
                toast(t("books.read.bookmarkAdded", "Bookmark added."));
            }
            renderBookmarks();
            syncBookmarkButton();
        } catch (error) {
            failed(error);
        }
    }

    bookmarkButton?.addEventListener("click", toggleBookmark);

    // ---- Contents panel ---------------------------------------------------------------

    const chapterList = root.querySelector("[data-book-chapter-list]");
    const bookmarkList = root.querySelector("[data-book-bookmark-list]");
    const highlightList = root.querySelector("[data-book-highlight-list]");
    const chapterSearch = root.querySelector("[data-book-chapter-search]");
    let chaptersLoaded = false;
    let annotationLoading = null;

    const message = value => {
        const item = document.createElement("li");
        item.className = "reader-contents-empty";
        item.textContent = value;
        return item;
    };

    const closeOverlayContents = () => {
        const contents = root.querySelector("[data-reader-contents]");
        if (contents?.classList.contains("is-overlay")) {
            root.querySelector("[data-reader-contents-close]")?.click();
        }
    };

    // PDF: the document outline, or page ranges when it has none.
    const loadPdfContents = async query => {
        const items = await pdf.contents(query);
        chapterList.replaceChildren();
        if (!items.length) {
            chapterList.append(message(t("books.read.noChapters", "No chapters found.")));
            return;
        }
        const [first] = pdf.visiblePages();
        const section = pdf.sectionFor(first) ?? items.filter(entry => entry.page <= first).at(-1);
        let current = null;
        for (const entry of items) {
            const item = document.createElement("li");
            const link = document.createElement("a");
            link.className = "reader-contents-row";
            link.href = readerUrl(pdf.positionForPage(entry.page).chapterId);
            if (entry.depth) link.style.setProperty("--book-contents-depth", String(entry.depth));
            const number = document.createElement("span");
            number.className = "reader-contents-index";
            number.textContent = String(entry.page);
            const title = document.createElement("span");
            title.className = "reader-contents-title";
            title.textContent = entry.title;
            link.append(number, title);
            if (entry === section) {
                link.setAttribute("aria-current", "page");
                current = link;
            }
            link.addEventListener("click", event => {
                event.preventDefault();
                void pdf.goToPage(entry.page, { animate: true });
                closeOverlayContents();
            });
            item.append(link);
            chapterList.append(item);
        }
        chaptersLoaded = true;
        current?.scrollIntoView({ block: "center" });
    };

    const loadChapters = async query => {
        if (!chapterList) return;
        chapterList.replaceChildren(message(t("books.read.loading", "Loading…")));
        try {
            if (pdfContainer) {
                if (pdf?.isReady()) await loadPdfContents(query);
                else chapterList.replaceChildren();
                return;
            }
            const result = await getJson("Chapters", { q: query || "" });
            const chapters = prop(result, "chapters") || [];
            chapterList.replaceChildren();
            if (!chapters.length) {
                chapterList.append(message(t("books.read.noChapters", "No chapters found.")));
                return;
            }
            let current = null;
            for (const chapter of chapters) {
                const id = String(prop(chapter, "id") || "");
                const item = document.createElement("li");
                const link = document.createElement("a");
                link.className = "reader-contents-row";
                link.href = readerUrl(id);
                const number = document.createElement("span");
                number.className = "reader-contents-index";
                number.textContent = String(prop(chapter, "number"));
                const title = document.createElement("span");
                title.className = "reader-contents-title";
                title.textContent = prop(chapter, "title") ||
                    t("books.chapter.number", "Chapter {number}", { number: prop(chapter, "number") });
                link.append(number, title);
                if (id.toLowerCase() === currentChapterId()) {
                    link.setAttribute("aria-current", "page");
                    current = link;
                }
                item.append(link);
                chapterList.append(item);
            }
            chaptersLoaded = true;
            current?.scrollIntoView({ block: "center" });
        } catch (error) {
            chapterList.replaceChildren(message(t("books.read.actionFailed", "That did not work. Please try again.")));
            console.warn(error);
        }
    };

    const ensureAnnotations = () => {
        if (allAnnotations) return Promise.resolve(allAnnotations);
        if (annotationLoading) return annotationLoading;
        annotationLoading = getJson("Annotations")
            .then(value => {
                allAnnotations = value || {};
                if (pdf) {
                    chapterBookmarks = (prop(allAnnotations, "bookmarks") || []).map(bookmarkItem);
                    syncBookmarkButton();
                }
                renderBookmarks();
                renderHighlightsList();
                return allAnnotations;
            })
            .finally(() => {
                annotationLoading = null;
            });
        return annotationLoading;
    };

    const chapterHeading = item =>
        t("books.chapter.number", "Chapter {number}", { number: prop(item, "chapterNumber") }) +
        (prop(item, "chapterTitle") ? " · " + prop(item, "chapterTitle") : "");

    const deleteButton = onDelete => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "reader-contents-delete";
        button.textContent = t("books.read.delete", "Delete");
        button.addEventListener("click", async () => {
            button.disabled = true;
            try {
                await onDelete();
            } catch (error) {
                button.disabled = false;
                failed(error);
            }
        });
        return button;
    };

    function renderBookmarks() {
        if (!bookmarkList || !allAnnotations) return;
        const bookmarks = prop(allAnnotations, "bookmarks") || [];
        bookmarkList.replaceChildren();
        if (!bookmarks.length) {
            bookmarkList.append(message(t("books.read.noBookmarks", "No bookmarks yet.")));
            return;
        }
        for (const bookmark of bookmarks) {
            const id = String(prop(bookmark, "id"));
            const chapterId = String(prop(bookmark, "chapterId") || "");
            const position = Number(prop(bookmark, "positionPermille") || 0);
            const language = prop(bookmark, "language");
            const item = document.createElement("li");
            item.className = "reader-contents-note";
            const link = document.createElement("a");
            link.className = "reader-contents-row";
            link.href = readerUrl(chapterId, { position, requestedView: viewForLanguage(language) });
            const title = document.createElement("span");
            title.className = "reader-contents-title";
            const meta = document.createElement("small");
            const page = pdf ? pdfPageOf(bookmark) : null;
            if (pdf) {
                title.textContent = page
                    ? t("books.read.pageNumber", "Page {number}", { number: page })
                    : chapterHeading(bookmark);
                meta.textContent = (page && pdf.sectionFor(page)?.title) || "";
            } else {
                title.textContent = chapterHeading(bookmark);
                meta.textContent = Math.round(position / 10) + "%";
            }
            link.append(title, meta);
            link.addEventListener("click", event => {
                if (pdf) {
                    if (!page) return;
                    event.preventDefault();
                    void pdf.goToPage(page, { animate: true });
                    closeOverlayContents();
                    return;
                }
                if (chapterId.toLowerCase() !== currentChapterId()) return;
                event.preventDefault();
                ensureViewForLanguage(language);
                requestAnimationFrame(() => jumpToPermille(position));
                closeOverlayContents();
            });
            item.append(link, deleteButton(async () => {
                if (repository) {
                    await repository.queueBookmarkRemove(id, { chapterId: chapterId || root.dataset.chapterId });
                } else {
                    await postHandler("RemoveBookmark", { bookmarkId: id });
                }
                allAnnotations.bookmarks = bookmarks.filter(entry => String(prop(entry, "id")) !== id);
                chapterBookmarks = chapterBookmarks.filter(entry => entry.id !== id);
                renderBookmarks();
                syncBookmarkButton();
                toast(t("books.read.bookmarkRemoved", "Bookmark removed."));
            }));
            bookmarkList.append(item);
        }
    }

    function renderHighlightsList() {
        if (!highlightList || !allAnnotations) return;
        const highlights = prop(allAnnotations, "highlights") || [];
        highlightList.replaceChildren();
        if (!highlights.length) {
            highlightList.append(message(t("books.read.noHighlights", "Select text to highlight it or add a note.")));
            return;
        }
        for (const highlight of highlights) {
            const id = String(prop(highlight, "id"));
            const chapterId = String(prop(highlight, "chapterId") || "");
            const paragraphIndex = Number(prop(highlight, "paragraphIndex") || 0);
            const language = prop(highlight, "language");
            const item = document.createElement("li");
            item.className = "reader-contents-note";
            const link = document.createElement("a");
            link.className = "reader-contents-row";
            link.href = readerUrl(chapterId, {
                paragraph: paragraphIndex,
                requestedView: viewForLanguage(language)
            });
            const title = document.createElement("span");
            title.className = "reader-contents-title";
            title.textContent = chapterHeading(highlight);
            const quote = document.createElement("q");
            quote.textContent = prop(highlight, "text") || "";
            link.append(title, quote);
            const note = prop(highlight, "note");
            if (note) {
                const noteText = document.createElement("small");
                noteText.textContent = note;
                link.append(noteText);
            }
            link.addEventListener("click", event => {
                if (chapterId.toLowerCase() !== currentChapterId()) return;
                event.preventDefault();
                ensureViewForLanguage(language);
                requestAnimationFrame(() => jumpToParagraph(paragraphIndex));
                closeOverlayContents();
            });
            item.append(link, deleteButton(async () => {
                await postHandler("RemoveHighlight", { highlightId: id });
                allAnnotations.highlights = highlights.filter(entry => String(prop(entry, "id")) !== id);
                currentHighlights = currentHighlights.filter(entry => String(prop(entry, "id")) !== id);
                renderHighlightsList();
                applyHighlights();
                toast(t("books.read.highlightRemoved", "Highlight removed."));
            }));
            highlightList.append(item);
        }
    }

    root.addEventListener("jularr:reader-contents", event => {
        const tab = event.detail?.tab;
        if (!event.detail?.open) return;
        if (tab === "chapters" && !chaptersLoaded) void loadChapters("");
        if (tab === "bookmarks" || tab === "notes") {
            const list = tab === "bookmarks" ? bookmarkList : highlightList;
            if (!allAnnotations && list && !list.childElementCount) {
                list.append(message(t("books.read.loading", "Loading…")));
            }
            ensureAnnotations().catch(error => {
                list?.replaceChildren(message(t("books.read.actionFailed", "That did not work. Please try again.")));
                console.warn(error);
            });
        }
    });

    let chapterTimer = 0;
    chapterSearch?.addEventListener("input", () => {
        window.clearTimeout(chapterTimer);
        chapterTimer = window.setTimeout(() => loadChapters(chapterSearch.value || ""), 200);
    });

    // ---- In-book search ---------------------------------------------------------------

    const searchForm = root.querySelector("[data-book-search-form]");
    const searchInput = root.querySelector("[data-book-search-input]");
    const searchStatus = root.querySelector("[data-book-search-status]");
    const searchResults = root.querySelector("[data-book-search-results]");
    let searchTimer = 0;
    let searchRun = 0;

    const renderSearch = hits => {
        searchResults.replaceChildren();
        searchStatus.textContent = hits.length ? "" : t("books.read.searchEmpty", "No matches.");
        searchStatus.hidden = hits.length > 0;
        for (const hit of hits) {
            const chapterId = String(prop(hit, "chapterId"));
            const paragraphIndex = Number(prop(hit, "paragraphIndex") || 0);
            const language = prop(hit, "language");
            const snippet = String(prop(hit, "snippet") || "");
            const start = Number(prop(hit, "matchStart") || 0);
            const length = Number(prop(hit, "matchLength") || 0);
            const item = document.createElement("li");
            const link = document.createElement("a");
            link.href = readerUrl(chapterId, {
                paragraph: paragraphIndex,
                requestedView: viewForLanguage(language)
            });
            const heading = document.createElement("strong");
            heading.textContent = t("books.chapter.number", "Chapter {number}", {
                number: prop(hit, "chapterNumber")
            }) + " · " + (prop(hit, "chapterTitle") || "");
            const quote = document.createElement("span");
            const mark = document.createElement("mark");
            mark.textContent = snippet.slice(start, start + length);
            quote.append(snippet.slice(0, start), mark, snippet.slice(start + length));
            link.append(heading, quote);
            link.addEventListener("click", event => {
                if (chapterId.toLowerCase() !== currentChapterId()) return;
                event.preventDefault();
                root.readerShell?.closeMenus(false);
                ensureViewForLanguage(language);
                requestAnimationFrame(() => jumpToParagraph(paragraphIndex));
            });
            item.append(link);
            searchResults.append(item);
        }
    };

    // PDF hits come from the pdf.js text of each page; a hit opens its page and
    // marks the match in the text layer.
    const renderPdfSearch = hits => {
        searchResults.replaceChildren();
        searchStatus.textContent = hits.length ? "" : t("books.read.searchEmpty", "No matches.");
        searchStatus.hidden = hits.length > 0;
        for (const hit of hits) {
            const item = document.createElement("li");
            const link = document.createElement("a");
            link.href = readerUrl(pdf.positionForPage(hit.page).chapterId);
            const heading = document.createElement("strong");
            const section = pdf.sectionFor(hit.page)?.title;
            heading.textContent = t("books.read.pageNumber", "Page {number}", { number: hit.page }) +
                (section ? " · " + section : "");
            const quote = document.createElement("span");
            const mark = document.createElement("mark");
            mark.textContent = hit.snippet.slice(hit.matchStart, hit.matchStart + hit.matchLength);
            quote.append(
                hit.snippet.slice(0, hit.matchStart),
                mark,
                hit.snippet.slice(hit.matchStart + hit.matchLength));
            link.append(heading, quote);
            link.addEventListener("click", event => {
                event.preventDefault();
                root.readerShell?.closeMenus(false);
                pdf.showHit(hit);
            });
            item.append(link);
            searchResults.append(item);
        }
    };

    const runSearch = async () => {
        const query = (searchInput?.value || "").trim();
        const run = ++searchRun;
        if (query.length < 2) {
            searchResults.replaceChildren();
            searchStatus.hidden = false;
            searchStatus.textContent = t("books.read.searchHint", "Type at least two characters.");
            return;
        }
        searchStatus.hidden = false;
        searchStatus.textContent = t("books.read.loading", "Loading…");
        try {
            if (pdfContainer) {
                const hits = pdf?.isReady() ? await pdf.search(query) : [];
                if (run !== searchRun || !hits) return;
                renderPdfSearch(hits);
                return;
            }
            const result = await getJson("Search", { q: query });
            if (run !== searchRun) return;
            renderSearch(prop(result, "hits") || []);
        } catch (error) {
            if (run !== searchRun) return;
            searchStatus.textContent = t("books.read.actionFailed", "That did not work. Please try again.");
            console.warn(error);
        }
    };

    searchInput?.addEventListener("input", () => {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(runSearch, 300);
    });
    searchForm?.addEventListener("submit", event => {
        event.preventDefault();
        window.clearTimeout(searchTimer);
        void runSearch();
    });

    // ---- Highlights and notes ------------------------------------------------------

    const selectionToolbar = root.querySelector("[data-book-selection-toolbar]");
    const highlightButton = root.querySelector("[data-book-highlight-button]");
    const noteButton = root.querySelector("[data-book-note-button]");
    const noteForm = root.querySelector("[data-book-note-form]");
    const noteInput = root.querySelector("[data-book-note-input]");
    const highlightForm = root.querySelector("[data-book-highlight-form]");
    let currentHighlights = readJson("[data-book-highlights-json]", []) || [];
    let selectionState = null;

    const paragraphForNode = node => {
        const element = node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement;
        return element?.closest?.("p[data-book-paragraph]") || null;
    };

    const offsetWithin = (paragraph, node, offset) => {
        const range = document.createRange();
        range.selectNodeContents(paragraph);
        range.setEnd(node, offset);
        return range.toString().length;
    };

    const hideSelectionToolbar = () => {
        selectionState = null;
        if (selectionToolbar) selectionToolbar.hidden = true;
        if (noteForm) noteForm.hidden = true;
    };

    const inspectSelection = () => {
        if (!selectionToolbar || (noteForm && !noteForm.hidden)) return;
        const selection = window.getSelection();
        if (!selection || selection.isCollapsed || !selection.rangeCount) {
            hideSelectionToolbar();
            return;
        }
        const range = selection.getRangeAt(0);
        const startParagraph = paragraphForNode(range.startContainer);
        const endParagraph = paragraphForNode(range.endContainer);
        const column = startParagraph?.closest("[data-book-language]");
        if (!startParagraph || startParagraph !== endParagraph || !column || !root.contains(column)) {
            hideSelectionToolbar();
            return;
        }
        const start = offsetWithin(startParagraph, range.startContainer, range.startOffset);
        const end = offsetWithin(startParagraph, range.endContainer, range.endOffset);
        if (end <= start) {
            hideSelectionToolbar();
            return;
        }
        selectionState = {
            language: column.dataset.bookLanguage || "original",
            paragraphIndex: Number(startParagraph.dataset.bookParagraph || "0"),
            startOffset: start,
            endOffset: end
        };
        const rect = range.getBoundingClientRect();
        selectionToolbar.hidden = false;
        const width = selectionToolbar.offsetWidth || 180;
        selectionToolbar.style.left =
            clamp(rect.left + rect.width / 2 - width / 2, 8, window.innerWidth - width - 8) + "px";
        selectionToolbar.style.top = Math.max(8, rect.top - selectionToolbar.offsetHeight - 10) + "px";
    };

    const rangesFor = (language, paragraphIndex) =>
        currentHighlights
            .filter(item =>
                String(prop(item, "language") || "") === language &&
                Number(prop(item, "paragraphIndex")) === paragraphIndex)
            .map(item => ({
                id: prop(item, "id"),
                start: Number(prop(item, "startOffset") || 0),
                end: Number(prop(item, "endOffset") || 0),
                note: prop(item, "note")
            }))
            .sort((a, b) => a.start - b.start);

    const renderParagraphHighlights = (paragraph, language, paragraphIndex) => {
        const content = paragraph.textContent || "";
        const ranges = rangesFor(language, paragraphIndex);
        if (!ranges.length) {
            if (paragraph.querySelector("mark[data-book-highlight]")) paragraph.textContent = content;
            return;
        }
        const fragment = document.createDocumentFragment();
        let cursor = 0;
        for (const item of ranges) {
            const start = Math.max(cursor, Math.min(content.length, item.start));
            const end = Math.max(start, Math.min(content.length, item.end));
            if (end <= start) continue;
            if (start > cursor) fragment.append(document.createTextNode(content.slice(cursor, start)));
            const mark = document.createElement("mark");
            mark.dataset.bookHighlight = String(item.id || "");
            mark.textContent = content.slice(start, end);
            if (item.note) {
                mark.title = item.note;
                mark.classList.add("has-note");
            }
            fragment.append(mark);
            cursor = end;
        }
        if (cursor < content.length) fragment.append(document.createTextNode(content.slice(cursor)));
        paragraph.replaceChildren(fragment);
    };

    const ensureParagraphMetadata = column => {
        column?.querySelectorAll("p").forEach((paragraph, index) => {
            paragraph.dataset.bookParagraph = String(index);
        });
    };

    function applyHighlights() {
        for (const column of [original, translated]) {
            if (!column) continue;
            ensureParagraphMetadata(column);
            const language = column.dataset.bookLanguage || "original";
            column.querySelectorAll("p[data-book-paragraph]").forEach(paragraph => {
                renderParagraphHighlights(paragraph, language, Number(paragraph.dataset.bookParagraph || "0"));
            });
        }
    }

    const createHighlight = async note => {
        if (!selectionState || !highlightForm) return;
        const data = new FormData(highlightForm);
        data.set("anchorLanguage", selectionState.language);
        data.set("paragraphIndex", String(selectionState.paragraphIndex));
        data.set("startOffset", String(selectionState.startOffset));
        data.set("endOffset", String(selectionState.endOffset));
        data.set("note", note || "");
        const response = await fetch(highlightForm.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" }
        });
        if (!response.ok) throw await httpError(response);
        const item = await response.json();
        currentHighlights.push(item);
        if (allAnnotations) {
            allAnnotations.highlights = (prop(allAnnotations, "highlights") || []).concat([item]);
            renderHighlightsList();
        }
        window.getSelection()?.removeAllRanges();
        hideSelectionToolbar();
        applyHighlights();
        toast(t("books.read.highlightSaved", "Highlight saved."));
    };

    document.addEventListener("selectionchange", () => {
        window.clearTimeout(inspectSelection.timer);
        inspectSelection.timer = window.setTimeout(inspectSelection, 60);
    });

    // Keep the selection while a toolbar button is pressed.
    selectionToolbar?.addEventListener("pointerdown", event => {
        if (event.target.closest("button") && !event.target.closest("form")) event.preventDefault();
    });

    highlightButton?.addEventListener("click", async () => {
        highlightButton.disabled = true;
        try {
            await createHighlight("");
        } catch (error) {
            failed(error);
        } finally {
            highlightButton.disabled = false;
        }
    });

    noteButton?.addEventListener("click", () => {
        if (!noteForm || !selectionState) return;
        noteForm.hidden = false;
        noteInput.value = "";
        noteInput.focus();
    });

    noteForm?.addEventListener("submit", async event => {
        event.preventDefault();
        const submit = noteForm.querySelector("button");
        if (submit) submit.disabled = true;
        try {
            await createHighlight((noteInput.value || "").trim());
        } catch (error) {
            failed(error);
        } finally {
            if (submit) submit.disabled = false;
        }
    });

    noteForm?.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            event.stopPropagation();
            hideSelectionToolbar();
        }
    });

    // ---- Offline chapter navigation (#221 part 2) -------------------------------------

    // A PDF book has all its pages open; there is no chapter to navigate to.
    if (workId && window.JularrOfflineLibraryRepository && !pdfContainer) {
        // While offline, following a previous/next chapter link to a downloaded
        // chapter renders it locally into the original column instead of a
        // failing full-page navigation; see docs/OFFLINE_LIBRARY.md.
        window.JularrOfflineLibraryRepository.initializeOfflineChapterNavigation({
            shell: root,
            workId,
            linkSelector: "a[data-book-chapter-link]",
            contentSelector: "[data-book-original]",
            renderer: "book"
        });

        root.addEventListener("jularr:offline-chapter-changed", event => {
            const payload = event.detail?.payload || {};
            if (translated) translated.replaceChildren();
            root.dataset.hasTranslation = "false";
            root.querySelectorAll('[data-reader-view="translated"], [data-reader-view="both"]')
                .forEach(button => {
                    button.hidden = true;
                });
            const title = payload.title || "";
            root.querySelectorAll("[data-book-chapter-title],[data-book-opening-title]").forEach(node => {
                node.textContent = title;
            });
            if (payload.number !== undefined) {
                root.dataset.chapterNumber = String(payload.number);
                const chapterLabel = t("books.chapter.number", "Chapter {number}", { number: payload.number });
                root.querySelectorAll("[data-book-chapter-number],[data-book-chapter-context]").forEach(node => {
                    node.textContent = chapterLabel;
                });
            }
            currentHighlights = [];
            chapterBookmarks = [];
            chaptersLoaded = false;
            root.dataset.progress = "0";
            view = "original";
            root.dataset.view = "original";
            if (original) original.hidden = false;
            if (translated) translated.hidden = true;
            syncViewControls();
            ensureParagraphMetadata(original);
            scheduleLayout(null);
        });
    }

    // ---- PDF pages -------------------------------------------------------------------

    function startPdf() {
        const status = root.querySelector("[data-book-pdf-status]");
        const positionLabel = root.querySelector("[data-book-pdf-position]");
        const sectionSteps = root.querySelectorAll("[data-book-pdf-section]");
        const pages = (readJson("[data-book-pdf-pages-json]", []) || []).map(String);
        const source = pdfContainer.dataset;
        if (!window.JularrBookPdf || !source.pdfFile || !pages.length) {
            if (status) {
                status.hidden = false;
                status.textContent = t("books.read.pdfFailed", "This book could not be opened.");
            }
            finishRestore();
            return;
        }

        pdf = window.JularrBookPdf.create({
            root,
            stage,
            container: pdfContainer,
            status,
            settings,
            t,
            pageChapters: pages,
            startChapterId: root.dataset.chapterId,
            startPermille: Number(root.dataset.progress || "0"),
            urls: {
                file: source.pdfFile,
                lib: source.pdfLib,
                worker: source.pdfWorker,
                cmaps: source.pdfCmaps,
                fonts: source.pdfFonts,
                wasm: source.pdfWasm
            },
            onLocation: ({ first, last, total, section }) => {
                dispatchLocation({
                    first,
                    last,
                    total,
                    value: last,
                    max: total,
                    percent: Math.round(last / total * 100)
                });
                if (positionLabel) {
                    const position = t("books.read.pagePosition", "Page {page} of {total}", {
                        page: first === last ? first : first + "–" + last,
                        total
                    });
                    positionLabel.textContent = section?.title ? position + " · " + section.title : position;
                }
                if (!restoring) queueProgressSave();
                if (view === "translated") void loadPdfTranslation();
            },
            // Without an outline the transport buttons step through pages.
            onOutline: items => {
                const chapters = items.length > 0;
                sectionSteps.forEach(button => {
                    const next = Number(button.dataset.bookPdfSection) > 0;
                    const label = chapters
                        ? next
                            ? t("reader.frame.nextChapter", "Next chapter")
                            : t("reader.frame.previousChapter", "Previous chapter")
                        : next
                            ? t("reader.frame.nextPage", "Next page")
                            : t("reader.frame.previousPage", "Previous page");
                    button.setAttribute("aria-label", label);
                    button.title = label;
                });
            },
            onReady: ready => {
                finishRestore();
                // Contents restored open before the pages were ready would otherwise stay empty.
                if (ready && !chaptersLoaded && !root.querySelector("[data-reader-contents]")?.hidden) {
                    void loadChapters(chapterSearch?.value);
                }
                if (ready) ensureAnnotations().catch(error => console.warn(error));
                if (pdfTranslation) pdfTranslation.hidden = view !== "translated";
                void loadPdfTranslation();
            }
        });

        sectionSteps.forEach(button => {
            button.addEventListener("click", () => pdf.stepSection(Number(button.dataset.bookPdfSection)));
        });
    }

    // ---- Start ---------------------------------------------------------------------

    root.dispatchEvent(new CustomEvent("jularr:reader-restoring", { detail: { active: true } }));

    if (pdfContainer) {
        startPdf();
        applySettings();
        return;
    }

    if (original) original.hidden = view === "translated";
    if (translated) translated.hidden = view === "original" || root.dataset.hasTranslation !== "true";
    syncViewControls();
    applyHighlights();

    const requestedParagraph = root.dataset.anchorParagraph;
    if (requestedParagraph !== undefined && requestedParagraph !== "") {
        pendingAnchor = { index: Number(requestedParagraph), flash: true };
    }
    applySettings();
})();
