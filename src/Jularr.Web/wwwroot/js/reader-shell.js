(() => {
    const roots = document.querySelectorAll("[data-unified-reader]");
    if (!roots.length) return;

    const interactiveSelector =
        "a,button,input,select,textarea,summary,label,[contenteditable='true'],[role='button']";

    const humanType = value => ({
        "book": "Bücher",
        "light-novel": "Light Novels",
        "web-novel": "Web Novels",
        "manga": "Manga",
        "fixed": "Fixed Documents"
    }[value] || value || "Bücher");

    const humanSource = source => {
        if (!source || source === "system") return "System-Preset";
        if (source === "default") return "Mein globaler Standard";
        if (source.startsWith("type:")) return humanType(source.slice(5));
        if (source.startsWith("genre:")) {
            const parts = source.split(":");
            return "Genre " + (parts.at(-1) || "").replaceAll("-", " ");
        }
        if (source.startsWith("work:")) return "Dieses Buch";
        return source;
    };

    for (const root of roots) {
        const surface =
            root.querySelector("[data-reader-surface]") ||
            root.querySelector("[data-reader-content]") ||
            root.querySelector("[data-book-reader-content]");
        const settingsContainer =
            root.querySelector("[data-reader-settings-container]") ||
            root.querySelector("[data-reader-settings-panel]") ||
            root.querySelector(".book-reader-settings");
        const settingsForm =
            root.querySelector("[data-reader-settings-form]") ||
            root.querySelector("[data-book-settings-form]");
        const settingsJson =
            root.querySelector("[data-reader-settings-json]") ||
            root.querySelector("[data-book-settings-json]");
        const topChrome =
            root.querySelector("[data-reader-chrome-primary]") ||
            root.querySelector(".novel-reader-toolbar") ||
            root.querySelector(".book-reader-toolbar");

        let settings = {};
        try {
            settings = JSON.parse(settingsJson?.textContent || "{}") || {};
        } catch {
            settings = {};
        }

        let restoring = true;
        let pointerStart = null;
        let lastScrollY = window.scrollY;
        let accumulatedScroll = 0;
        let activeSettingsTab = "reading";

        // Reader frame (docs/UNIFIED_READER.md "Reader frame"): pages that render
        // the shared top bar, contents panel, bottom bar and mobile tool row opt
        // in with data-reader-frame. The shell then owns their menus, contents
        // panel, settings openers, progress slider, fullscreen, timer and share
        // instead of generating its own mobile action bar and overflow menu.
        const frame = root.hasAttribute("data-reader-frame");
        let frameText = {};
        try {
            frameText = JSON.parse(
                root.querySelector("[data-reader-frame-text]")?.textContent || "{}") || {};
        } catch {
            frameText = {};
        }
        const ft = (key, fallback, values = {}) => {
            let value = frameText["reader.frame." + key] || fallback;
            for (const [name, replacement] of Object.entries(values)) {
                value = value.replaceAll("{" + name + "}", String(replacement));
            }
            return value;
        };
        const compactQuery = window.matchMedia("(max-width: 720px)");
        // Tablet landscape keeps Contents beside the page; below that it is a modal sheet.
        const inlineContentsQuery = window.matchMedia("(min-width: 1000px)");
        const detailPanel = document.querySelector("[data-language-inspector]");

        root.classList.remove("reader-chrome-hidden");
        root.dataset.readerChrome = "visible";
        // Books and Novels (prose frames) hide their bars on every device and keep the page hidden until the exact
        // resume position is applied, so there is no visible jump. Other frames keep their phone-only behaviour.
        const proseFrame = frame && root.classList.contains("reader-frame-prose");
        if (proseFrame) root.dataset.readerRestoring = "true";

        const setRestoring = active => {
            restoring = active;
            if (proseFrame) root.dataset.readerRestoring = active ? "true" : "false";
        };

        const detailOpen = () => Boolean(detailPanel) && !detailPanel.hidden;

        const overlayOpen = () => {
            if (settingsContainer?.open || detailOpen()) return true;
            if (root.querySelector("dialog[open]")) return true;
            return Boolean(root.querySelector(
                "[data-reader-notes]:not([hidden])," +
                "[data-chapter-drawer]:not([hidden])," +
                "[data-book-drawer][open]," +
                "[data-reader-menu]:not([hidden])," +
                "[data-reader-contents].is-overlay:not([hidden])"));
        };

        const showChrome = () => {
            root.classList.remove("reader-chrome-hidden");
            root.dataset.readerChrome = "visible";
        };

        const readingMode = () =>
            root.dataset.readingMode || settings.readingMode || "continuous";

        // Hiding the bars never changes the page area, so the logical reading
        // position is the same with and without chrome (docs/mockups/reader/SPEC.md).
        // Image readers explicitly opt into immersive chrome through the shared
        // runtime instead of owning a second hidden-state class.
        const frameChromeCanHide = () =>
            root.dataset.readerImmersive === "true" ||
            proseFrame ||
            (compactQuery.matches && readingMode() !== "paged");

        const hideChrome = () => {
            if (frame && !frameChromeCanHide()) return;
            if (restoring || overlayOpen()) return;
            root.classList.add("reader-chrome-hidden");
            root.dataset.readerChrome = "hidden";
        };

        const toggleChrome = () => {
            if (root.classList.contains("reader-chrome-hidden")) showChrome();
            else hideChrome();
        };

        const dispatchPage = direction => {
            root.dispatchEvent(new CustomEvent("jularr:reader-page-edge", {
                detail: { direction },
                bubbles: false
            }));
        };

        const dispatchSeek = value => {
            root.dispatchEvent(new CustomEvent("jularr:reader-seek", {
                detail: { value },
                bubbles: false
            }));
        };

        const pageDirection = () => {
            const direction = (root.dataset.pageDirection || root.dataset.direction || "ltr")
                .trim()
                .toLowerCase();
            return direction === "rtl" ? "rtl" : "ltr";
        };

        const dispatchPhysicalPage = direction =>
            dispatchPage(pageDirection() === "rtl" ? -direction : direction);

        const updateModeVisibility = () => {
            const mode = readingMode();
            root.querySelectorAll("[data-reader-mode-choice]").forEach(button => {
                const active = button.dataset.readerModeChoice === mode;
                button.classList.toggle("is-active", active);
                button.setAttribute("aria-pressed", active ? "true" : "false");
            });
            root.querySelectorAll("[data-reader-mode-only]").forEach(element => {
                element.hidden = element.dataset.readerModeOnly !== mode;
            });
        };

        const controlFor = key =>
            settingsForm?.querySelector(
                `[data-reader-setting][data-setting-key="${CSS.escape(key)}"],` +
                `[data-book-setting="${CSS.escape(key)}"]`);

        const keyForControl = control =>
            control?.dataset.settingKey || control?.dataset.bookSetting || "";

        const advancedKeys = new Set([
            "themeEffectStrength", "themeBrightness", "themeContrast",
            "themeSaturation", "themeBlurPx", "themeVignetteStrength",
            "themeGrainStrength", "themeTextBackdropStrength",
            "themeParallaxStrength", "themeTintStrength"
        ]);

        const sectionForKey = key => {
            if ([
                "readingMode", "pageTransition", "twoPageSpread", "autoScrollSpeed",
                "showPageNumbers", "autoContinueChapters"
            ].includes(key)) return "reading";
            if ([
                "fontFamily", "fontSizeRem", "lineHeight", "paragraphSpacingEm",
                "textWidthPx", "textAlignment", "chapterStyle", "hyphenation", "paragraphIndent"
            ].includes(key)) return "text";
            if ([
                "paperStyle", "genreArtworkEnabled", "genreTheme", "showIllustrations",
                "backgroundAssetId", "backgroundIntensity", "backgroundMotionMode",
                "themeEffectStrength", "themeBrightness", "themeContrast",
                "themeSaturation", "themeBlurPx", "themeVignetteStrength",
                "themeGrainStrength", "themeTextBackdropStrength",
                "themeParallaxStrength", "themeTintStrength"
            ].includes(key)) return "appearance";
            return "defaults";
        };

        const wrapperFor = (control, key) => {
            if (advancedKeys.has(key)) {
                const details = control.closest(
                    ".novel-theme-advanced,.book-theme-advanced");
                if (details) return details;
            }
            return control.closest("label") || control;
        };

        const ensureHidden = (name, value = "") => {
            if (!settingsForm) return null;
            let input = settingsForm.querySelector(
                `input[type="hidden"][name="${CSS.escape(name)}"]`);
            if (!input) {
                input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                settingsForm.append(input);
            }
            input.value = value;
            return input;
        };

        const selectedTarget = () =>
            settingsForm?.querySelector('[name="scope"]')?.value || "work";

        const selectedGenre = () =>
            settingsForm?.querySelector('[name="genre"]')?.value || "";

        const setTarget = (target, genre = "") => {
            const scope = ensureHidden("scope", target);
            ensureHidden("genre", genre);
            ensureHidden("genrePriority", "500");
            if (scope) scope.value = target;
            root.dataset.readerPreferenceTarget = target;
            root.dataset.readerPreferenceGenre = genre;
            root.querySelectorAll("[data-reader-reset-field]").forEach(button => {
                button.title = target === "work"
                    ? "Für dieses Buch wieder erben"
                    : "Auf dieser Ebene wieder erben";
            });
        };

        const applySettingsPayload = next => {
            if (!next) return;
            settings = next;
            if (next.readingMode) root.dataset.readingMode = next.readingMode;
            updateModeVisibility();
            updateSourceBadges();
            root.dispatchEvent(new CustomEvent("jularr:reader-settings-response", {
                detail: { settings: next }
            }));
        };

        const postSettingsCommand = async ({
            changedKey = "",
            resetField = false,
            resetScope = false
        } = {}) => {
            if (!settingsForm) return null;
            const data = new FormData(settingsForm);
            data.set("scope", selectedTarget());
            data.set("genre", selectedGenre());
            data.set(
                "genrePriority",
                settingsForm.querySelector('[name="genrePriority"]')?.value || "500");
            data.set("changedKey", changedKey);
            data.set("resetField", resetField ? "true" : "false");
            data.set("resetScope", resetScope ? "true" : "false");

            const response = await fetch(settingsForm.action, {
                method: "POST",
                body: data,
                credentials: "same-origin",
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) {
                throw new Error(
                    (await response.text()) || "Reader-Einstellungen konnten nicht gespeichert werden.");
            }

            const payload = await response.json();
            applySettingsPayload(payload?.settings);
            return payload?.settings || null;
        };

        const updateSourceBadges = () => {
            const sources = settings.effectiveSources || {};
            root.querySelectorAll("[data-reader-setting-source]").forEach(badge => {
                const key = badge.dataset.readerSettingSource;
                badge.textContent = humanSource(sources[key]);
            });
        };

        const addResetButton = (wrapper, key) => {
            if (!wrapper || wrapper.querySelector("[data-reader-reset-field]")) return;
            const button = document.createElement("button");
            button.type = "button";
            button.className = "reader-setting-reset";
            button.dataset.readerResetField = key;
            button.textContent = "↶";
            button.setAttribute("aria-label", "Diese Einstellung wieder erben");
            button.addEventListener("click", async event => {
                event.preventDefault();
                event.stopPropagation();
                try {
                    await postSettingsCommand({ changedKey: key, resetField: true });
                } catch (error) {
                    console.warn(error);
                }
            });
            wrapper.append(button);

            const badge = document.createElement("small");
            badge.className = "reader-setting-source";
            badge.dataset.readerSettingSource = key;
            wrapper.append(badge);
        };

        const enhanceSettings = () => {
            if (!settingsForm || settingsForm.dataset.unifiedSettingsReady === "true") {
                return;
            }
            settingsForm.dataset.unifiedSettingsReady = "true";
            settingsContainer?.setAttribute("data-reader-settings-container", "");

            const workspace = document.createElement("div");
            workspace.className = "reader-settings-workspace";

            const tabs = document.createElement("div");
            tabs.className = "reader-settings-tabs";
            tabs.setAttribute("role", "tablist");

            // The read-aloud section is server-rendered only when the document
            // supports TTS; the tab exists only when that section is present.
            const ttsSection = settingsForm.querySelector("[data-reader-tts-settings]");
            const tabDefinitions = [
                ["reading", "Lesen"],
                ["text", "Text"],
                ["appearance", "Aussehen"],
                ["defaults", "Defaults"]
            ];
            if (ttsSection) tabDefinitions.splice(3, 0, ["tts", "Vorlesen"]);

            const panels = {};
            for (const [key, label] of tabDefinitions) {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "reader-settings-tab";
                button.dataset.readerSettingsTab = key;
                button.textContent = label;
                button.setAttribute("role", "tab");
                button.addEventListener("click", () => activateTab(key));
                tabs.append(button);

                const panel = document.createElement("section");
                panel.className = "reader-settings-tab-panel";
                panel.dataset.readerSettingsPanel = key;
                panel.setAttribute("role", "tabpanel");
                panels[key] = panel;
            }

            workspace.append(tabs, ...tabDefinitions.map(([key]) => panels[key]));
            settingsForm.prepend(workspace);

            const heading = settingsForm.querySelector(".reader-settings-heading");
            if (heading) {
                workspace.before(heading);
            }

            const moved = new Set();
            const controls = Array.from(settingsForm.querySelectorAll(
                "[data-reader-setting],[data-book-setting]"));

            for (const control of controls) {
                const key = keyForControl(control);
                if (!key) continue;
                const wrapper = wrapperFor(control, key);
                if (!wrapper || moved.has(wrapper) || workspace.contains(wrapper)) continue;
                moved.add(wrapper);
                const section = sectionForKey(key);
                panels[section].append(wrapper);
                wrapper.dataset.readerSettingWrapper = key;
                addResetButton(wrapper, key);

                if (key === "readingMode") {
                    wrapper.classList.add("reader-native-mode-field");
                }
                if (key === "autoScrollSpeed") {
                    wrapper.dataset.readerModeOnly = "continuous";
                }
                if (key === "pageTransition" || key === "twoPageSpread") {
                    wrapper.dataset.readerModeOnly = "paged";
                }
            }

            const googleFont = settingsForm.querySelector(".novel-google-font");
            if (googleFont && !workspace.contains(googleFont)) {
                panels.text.append(googleFont);
            }

            if (ttsSection && !workspace.contains(ttsSection)) {
                panels.tts.append(ttsSection);
            }

            const actions = settingsForm.querySelector(
                ".novel-settings-actions,.book-settings-actions");
            if (actions && !workspace.contains(actions)) {
                actions.classList.add("reader-legacy-settings-actions");
                panels.defaults.append(actions);
            }

            settingsForm.querySelectorAll(
                ".novel-settings-group,.book-theme-advanced-grid").forEach(group => {
                if (!group.querySelector("input,select,button,textarea,details")) {
                    group.remove();
                }
            });

            // A document without controls for a section (fixed pages have no
            // typography) gets no tab for it.
            for (const [key] of tabDefinitions) {
                if (key === "reading" || key === "defaults" || panels[key].childElementCount) continue;
                tabs.querySelector(`[data-reader-settings-tab="${key}"]`)?.remove();
                panels[key].remove();
            }

            const modeControl = controlFor("readingMode");
            if (modeControl) {
                const switcher = document.createElement("div");
                switcher.className = "reader-mode-switch";
                switcher.setAttribute("role", "group");
                switcher.setAttribute("aria-label", "Lesemodus");
                for (const [value, label] of [["continuous", "Scrollen"], ["paged", "Seiten"]]) {
                    const button = document.createElement("button");
                    button.type = "button";
                    button.dataset.readerModeChoice = value;
                    button.textContent = label;
                    button.addEventListener("click", () => {
                        if (modeControl.value === value) return;
                        modeControl.value = value;
                        modeControl.dispatchEvent(new Event("change", { bubbles: true }));
                        root.dataset.readingMode = value;
                        settings.readingMode = value;
                        updateModeVisibility();
                    });
                    switcher.append(button);
                }
                panels.reading.prepend(switcher);
            }

            const sourceAuto =
                root.querySelector("[data-reader-autoscroll-toggle]") ||
                root.querySelector("[data-book-autoscroll]");
            if (sourceAuto) {
                const auto = document.createElement("button");
                auto.type = "button";
                auto.className = "reader-inline-action";
                auto.dataset.readerModeOnly = "continuous";
                auto.textContent = "Auto-Scroll starten / pausieren";
                auto.addEventListener("click", () => sourceAuto.click());
                panels.reading.append(auto);
            }

            const paperControl = controlFor("paperStyle");
            if (paperControl) {
                const swatches = document.createElement("div");
                swatches.className = "reader-paper-swatches";
                for (const option of Array.from(paperControl.options)) {
                    const button = document.createElement("button");
                    button.type = "button";
                    button.dataset.paper = option.value;
                    button.title = option.textContent || option.value;
                    button.setAttribute("aria-label", option.textContent || option.value);
                    button.addEventListener("click", () => {
                        paperControl.value = option.value;
                        paperControl.dispatchEvent(new Event("change", { bubbles: true }));
                    });
                    swatches.append(button);
                }
                paperControl.closest("label")?.append(swatches);
            }

            const targetBox = document.createElement("div");
            targetBox.className = "reader-default-target";
            const title = document.createElement("strong");
            title.textContent = "Änderungen speichern für";
            const targetSelect = document.createElement("select");
            targetSelect.dataset.readerPreferenceTarget = "";

            const addOption = (value, label, genre = "") => {
                const option = document.createElement("option");
                option.value = value + (genre ? ":" + genre : "");
                option.textContent = label;
                targetSelect.append(option);
            };

            addOption("work", "Dieses Buch");
            addOption("type", "Alle " + humanType(
                root.dataset.readerContentType || settings.contentTypeKey));
            for (const genre of settings.sourceGenres || []) {
                addOption("genre", "Genre: " + genre, genre);
            }
            addOption("default", "Mein globaler Standard");

            targetSelect.addEventListener("change", () => {
                const [target, ...rest] = targetSelect.value.split(":");
                setTarget(target, rest.join(":"));
                updateSourceBadges();
            });

            const resetScope = document.createElement("button");
            resetScope.type = "button";
            resetScope.className = "button";
            resetScope.textContent = "Diese Ebene zurücksetzen";
            resetScope.addEventListener("click", async () => {
                try {
                    await postSettingsCommand({ resetScope: true });
                } catch (error) {
                    console.warn(error);
                }
            });

            const copyCurrent = document.createElement("button");
            copyCurrent.type = "button";
            copyCurrent.className = "button";
            copyCurrent.textContent = "Aktuelle Werte auf diese Ebene kopieren";
            copyCurrent.addEventListener("click", async () => {
                try {
                    await postSettingsCommand();
                } catch (error) {
                    console.warn(error);
                }
            });

            targetBox.append(title, targetSelect, resetScope, copyCurrent);
            panels.defaults.prepend(targetBox);
            setTarget("work");

            activateTab(activeSettingsTab);
            updateModeVisibility();
            updateSourceBadges();
        };

        const activateTab = tab => {
            activeSettingsTab = tab;
            root.querySelectorAll("[data-reader-settings-tab]").forEach(button => {
                const active = button.dataset.readerSettingsTab === tab;
                button.classList.toggle("is-active", active);
                button.setAttribute("aria-selected", active ? "true" : "false");
            });
            root.querySelectorAll(
                '[data-reader-settings-panel][role="tabpanel"]'
            ).forEach(panel => {
                if (panel.closest("[data-reader-settings-form]") !== settingsForm) {
                    return;
                }
                panel.hidden = panel.dataset.readerSettingsPanel !== tab;
            });
        };

        const buildMobileActions = () => {
            if (frame || root.querySelector("[data-reader-mobile-actions]")) return;

            const nav = document.createElement("nav");
            nav.className = "reader-mobile-actions";
            nav.dataset.readerMobileActions = "";
            nav.dataset.readerChrome = "";
            nav.setAttribute("aria-label", "Reader");

            // A proxy only exists while the control it drives exists (#487: no
            // dead mobile buttons).
            const addProxy = (label, selectors, action) => {
                const source = root.querySelector(selectors);
                if (!source) return null;
                const button = document.createElement("button");
                button.type = "button";
                button.textContent = label;
                button.addEventListener("click", () => {
                    showChrome();
                    if (action) action(button, source);
                    else source.click();
                });
                nav.append(button);
                return button;
            };

            addProxy(
                "Kapitel",
                "[data-reader-chapters-toggle],[data-book-drawer-open]");

            // The language switch opens as a panel above the action row and
            // closes again from the same button, after a choice or on Escape.
            const setLanguageExpanded = (button, expanded) => {
                root.classList.toggle("reader-language-expanded", expanded);
                button.setAttribute("aria-expanded", expanded ? "true" : "false");
            };
            const languageButton = addProxy(
                "Sprache",
                "[data-reader-language-control]",
                button => setLanguageExpanded(
                    button,
                    !root.classList.contains("reader-language-expanded")));
            if (languageButton) {
                const languageControl = root.querySelector("[data-reader-language-control]");
                if (!languageControl.id) languageControl.id = "reader-language-control";
                languageButton.setAttribute("aria-controls", languageControl.id);
                languageButton.setAttribute("aria-expanded", "false");
                languageControl.addEventListener("click", event => {
                    if (event.target.closest("button")) setLanguageExpanded(languageButton, false);
                });
                root.addEventListener("keydown", event => {
                    if (event.key === "Escape" && root.classList.contains("reader-language-expanded")) {
                        setLanguageExpanded(languageButton, false);
                        languageButton.focus();
                    }
                });
            }
            addProxy(
                "Aa",
                "[data-reader-settings-container],.novel-reader-settings,.book-reader-settings",
                () => {
                    if (settingsContainer && "open" in settingsContainer) {
                        settingsContainer.open = true;
                    }
                });
            addProxy(
                "Notizen",
                "[data-reader-notes-toggle]");

            if (nav.childElementCount) root.append(nav);
        };

        const ensureOverflow = () => {
            if (frame) return root.querySelector('[data-reader-menu="more"]');
            if (!topChrome) return null;
            const existing = topChrome.querySelector("[data-reader-overflow]");
            if (existing) return existing.querySelector(".reader-overflow-menu");

            const wrap = document.createElement("details");
            wrap.className = "reader-overflow";
            wrap.dataset.readerOverflow = "";
            const summary = document.createElement("summary");
            summary.textContent = "•••";
            summary.setAttribute("aria-label", "Weitere Reader-Aktionen");
            const menu = document.createElement("div");
            menu.className = "reader-overflow-menu";
            wrap.append(summary, menu);
            const target =
                topChrome.querySelector(".novel-toolbar-end,.book-reader-toolbar") ||
                topChrome;
            target.append(wrap);
            return menu;
        };

        // Low-frequency actions live in the overflow menu (#286 hierarchy).
        const addOverflowAction = (label, onClick) => {
            const menu = ensureOverflow();
            if (!menu) return null;
            const button = document.createElement("button");
            button.type = "button";
            button.textContent = label;
            if (frame) {
                // role=menuitem: the frame closes the menu after activation.
                button.setAttribute("role", "menuitem");
                const settingsItem = menu.querySelector('[data-reader-settings-open]:last-child');
                menu.insertBefore(button, settingsItem || null);
                button.addEventListener("click", onClick);
                return button;
            }
            button.addEventListener("click", () => {
                onClick();
                const wrap = menu.closest("details");
                if (wrap) wrap.open = false;
            });
            menu.append(button);
            return button;
        };

        const addMobileAction = (label, onClick) => {
            // The frame renders its own mobile tool row (with read aloud).
            if (frame) return null;
            let nav = root.querySelector("[data-reader-mobile-actions]");
            if (!nav) {
                nav = document.createElement("nav");
                nav.className = "reader-mobile-actions";
                nav.dataset.readerMobileActions = "";
                nav.dataset.readerChrome = "";
                nav.setAttribute("aria-label", "Reader");
                root.append(nav);
            }
            const button = document.createElement("button");
            button.type = "button";
            button.textContent = label;
            button.addEventListener("click", () => {
                showChrome();
                onClick();
            });
            nav.append(button);
            return button;
        };

        const buildOverflow = () => {
            // The frame renders its own More menu with these actions.
            if (frame || !topChrome || topChrome.querySelector("[data-reader-overflow]")) return;
            const sources = [
                root.querySelector("[data-reader-wake-lock-toggle]"),
                root.querySelector("[data-reader-immersive-toggle]")
            ].filter(Boolean);

            for (const source of sources) {
                source.classList.add("reader-overflow-source");
                addOverflowAction(
                    source.dataset.readerWakeLockToggle !== undefined
                        ? "Bildschirm an"
                        : "Immersiv",
                    () => source.click());
            }
        };

        if (topChrome) {
            topChrome.dataset.readerChrome = "";
            topChrome.dataset.readerChromePrimary = "";
        }
        if (surface) surface.dataset.readerSurface = "";
        settingsContainer?.setAttribute("data-reader-settings-container", "");

        // ---- Reader frame -------------------------------------------------------

        const focusableSelector =
            "button:not([disabled]):not([hidden]),a[href],input:not([disabled]):not([type='hidden'])," +
            "select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex='-1'])";
        const isShown = element =>
            Boolean(element) && element.isConnected &&
            (typeof element.checkVisibility === "function"
                ? element.checkVisibility()
                : element.getClientRects().length > 0);
        const focusablesIn = container =>
            Array.from(container?.querySelectorAll(focusableSelector) || []).filter(isShown);

        const toastElement = () =>
            root.querySelector("[data-reader-toast],[data-book-toast]");
        const toast = message => {
            const element = toastElement();
            if (!element || !message) return;
            element.textContent = message;
            element.hidden = false;
            window.clearTimeout(toast.timer);
            toast.timer = window.setTimeout(() => {
                element.hidden = true;
            }, 2600);
        };

        let openMenu = null;
        let menuTrigger = null;
        let menuScrim = null;
        let notice = null;
        let noticeRetry = null;

        const menuToggles = name =>
            root.querySelectorAll(`[data-reader-menu-toggle="${CSS.escape(name)}"]`);

        const closeMenus = (restoreFocus = false) => {
            if (!openMenu) return;
            const name = openMenu.dataset.readerMenu;
            openMenu.hidden = true;
            menuToggles(name).forEach(button => button.setAttribute("aria-expanded", "false"));
            if (menuScrim) menuScrim.hidden = true;
            const trigger = menuTrigger;
            openMenu = null;
            menuTrigger = null;
            delete root.dataset.readerMenuOpen;
            root.dispatchEvent(new CustomEvent("jularr:reader-menu", {
                detail: { name, open: false }
            }));
            if (restoreFocus && isShown(trigger)) trigger.focus();
        };

        const openMenuNamed = (name, trigger) => {
            const menu = root.querySelector(`[data-reader-menu="${CSS.escape(name)}"]`);
            if (!menu) return;
            if (openMenu === menu) {
                closeMenus(true);
                return;
            }
            // A trigger inside another menu (More → Search) hands over focus.
            const origin = trigger?.closest("[data-reader-menu]") ? null : trigger;
            closeMenus(false);
            if (settingsContainer?.open) settingsContainer.open = false;
            menu.hidden = false;
            if (menu.getAttribute("role") === "dialog") {
                menu.setAttribute("aria-modal", compactQuery.matches ? "true" : "false");
            }
            openMenu = menu;
            menuTrigger = origin || root.querySelector(
                `[data-reader-menu-toggle="${CSS.escape(name)}"]:not([role])`);
            root.dataset.readerMenuOpen = name;
            menuToggles(name).forEach(button => button.setAttribute("aria-expanded", "true"));
            if (menuScrim) menuScrim.hidden = !compactQuery.matches;
            showChrome();
            root.dispatchEvent(new CustomEvent("jularr:reader-menu", {
                detail: { name, open: true }
            }));
            requestAnimationFrame(() => {
                const preferred = menu.querySelector("input[type='search'],[aria-checked='true']");
                (isShown(preferred) ? preferred : focusablesIn(menu)[0])?.focus();
            });
        };

        let contentsTrigger = null;
        const contents = root.querySelector("[data-reader-contents]");
        const contentsBackdrop = root.querySelector("[data-reader-contents-backdrop]");
        const contentsKey = "jularr:reader-contents-open";
        let activeContentsTab =
            contents?.querySelector("[data-reader-contents-tab][aria-selected='true']")
                ?.dataset.readerContentsTab || "chapters";

        const activateContentsTab = (name, focus = false) => {
            if (!contents) return;
            const tab = contents.querySelector(
                `[data-reader-contents-tab="${CSS.escape(name)}"]`);
            if (!tab) return;
            activeContentsTab = name;
            contents.querySelectorAll("[data-reader-contents-tab]").forEach(button => {
                const active = button === tab;
                button.setAttribute("aria-selected", active ? "true" : "false");
                button.tabIndex = active ? 0 : -1;
            });
            contents.querySelectorAll("[data-reader-contents-panel]").forEach(panel => {
                // One panel may serve several tabs (for example bookmarks and notes).
                panel.hidden = !panel.dataset.readerContentsPanel.split(" ").includes(name);
            });
            if (focus) tab.focus();
            if (!contents.hidden) {
                root.dispatchEvent(new CustomEvent("jularr:reader-contents", {
                    detail: { open: true, tab: name }
                }));
            }
        };

        const setContents = (open, { tab = null, focus = true, remember = true } = {}) => {
            if (!contents) return;
            const overlay = !inlineContentsQuery.matches;
            if (open) closeMenus(false);
            contents.hidden = !open;
            contents.classList.toggle("is-overlay", open && overlay);
            if (open && overlay) {
                contents.setAttribute("role", "dialog");
                contents.setAttribute("aria-modal", "true");
            } else {
                contents.removeAttribute("role");
                contents.removeAttribute("aria-modal");
            }
            if (contentsBackdrop) contentsBackdrop.hidden = !(open && overlay);
            root.classList.toggle("reader-contents-open", open);
            root.querySelectorAll("[data-reader-contents-toggle]").forEach(button => {
                button.setAttribute("aria-expanded", open ? "true" : "false");
            });
            if (tab) activateContentsTab(tab);
            if (open) {
                showChrome();
                root.dispatchEvent(new CustomEvent("jularr:reader-contents", {
                    detail: { open: true, tab: activeContentsTab }
                }));
                if (focus) {
                    contents.querySelector(
                        `[data-reader-contents-tab="${CSS.escape(activeContentsTab)}"]`)?.focus();
                }
            } else if (focus && isShown(contentsTrigger)) {
                contentsTrigger.focus();
            }
            if (remember && !overlay) {
                try {
                    window.localStorage.setItem(contentsKey, open ? "1" : "0");
                } catch {
                }
            }
            root.dispatchEvent(new CustomEvent("jularr:reader-layout"));
        };

        const openSettings = tab => {
            if (!settingsContainer) return;
            closeMenus(false);
            if (contents?.classList.contains("is-overlay")) setContents(false, { focus: false });
            settingsContainer.open = true;
            const available = root.querySelector(
                `[data-reader-settings-tab="${CSS.escape(tab || "reading")}"]`);
            activateTab(available ? tab : "reading");
            requestAnimationFrame(() => {
                root.querySelector(
                    `[data-reader-settings-tab="${CSS.escape(activeSettingsTab)}"]`)?.focus();
            });
        };

        const fullscreenSupported = () =>
            Boolean(document.fullscreenEnabled && root.requestFullscreen);

        const syncFullscreen = () => {
            const active = document.fullscreenElement === root;
            root.classList.toggle("reader-fullscreen", active);
            root.querySelectorAll("[data-reader-fullscreen-toggle]").forEach(button => {
                button.setAttribute("aria-pressed", active ? "true" : "false");
                const label = button.querySelector("[data-reader-fullscreen-label]");
                const text = active
                    ? ft("exitFullscreen", "Exit fullscreen")
                    : ft("fullscreen", "Fullscreen");
                if (label) label.textContent = text;
                else button.setAttribute("aria-label", text);
            });
        };

        const toggleFullscreen = async () => {
            try {
                if (document.fullscreenElement) await document.exitFullscreen();
                else await root.requestFullscreen();
            } catch (error) {
                console.warn(error);
            }
        };

        let timerEnd = 0;
        let timerTick = 0;
        const timerLabel = root.querySelector("[data-reader-timer-label]");
        const setTimer = minutes => {
            window.clearInterval(timerTick);
            timerEnd = minutes > 0 ? Date.now() + minutes * 60000 : 0;
            root.querySelectorAll("[data-reader-timer]").forEach(button => {
                button.setAttribute(
                    "aria-pressed",
                    Number(button.dataset.readerTimer) === minutes ? "true" : "false");
            });
            const render = () => {
                if (!timerLabel) return;
                timerLabel.textContent = timerEnd
                    ? ft("timerLeft", "{minutes} min left", {
                        minutes: Math.max(1, Math.ceil((timerEnd - Date.now()) / 60000))
                    })
                    : ft("timer", "Reading timer");
            };
            render();
            if (!timerEnd) return;
            timerTick = window.setInterval(() => {
                if (Date.now() < timerEnd) {
                    render();
                    return;
                }
                if (root.dataset.readerTts && root.dataset.readerTts !== "idle") {
                    root.querySelector("[data-reader-tts-stop]")?.click();
                }
                root.dispatchEvent(new CustomEvent("jularr:reader-timer-end"));
                setTimer(0);
                toast(ft("timerDone", "Reading timer ended."));
            }, 15000);
        };

        const shareUrl = () => {
            const url = new URL(window.location.href);
            for (const key of ["pos", "p", "handler"]) url.searchParams.delete(key);
            return url.toString();
        };

        const share = async () => {
            const url = shareUrl();
            try {
                if (navigator.share) {
                    await navigator.share({ title: document.title, url });
                    return;
                }
                await navigator.clipboard.writeText(url);
                toast(ft("linkCopied", "Link copied."));
            } catch (error) {
                if (error?.name !== "AbortError") console.warn(error);
            }
        };

        const progressSlider = root.querySelector("[data-reader-progress-slider]");
        const progressText = root.querySelector("[data-reader-progress-text]");
        let sliderDragging = false;

        const paintSlider = () => {
            if (!progressSlider) return;
            const max = Number(progressSlider.max) || 0;
            const fraction = max > 0 ? Number(progressSlider.value) / max : 0;
            progressSlider.style.setProperty("--reader-progress", (fraction * 100).toFixed(2) + "%");
        };

        // ---- Setting proxies ------------------------------------------------------
        // Quick controls outside the settings form (for example an appearance
        // sheet) mirror a canonical settings control by key:
        //   data-reader-proxy="key"                 range / checkbox / button
        //   data-value="v"                          button that selects v
        //   data-values='{"a":"x","b":"y"}'         button that sets several keys
        //   data-on / data-off                      checkbox mapped to two values
        // They change the canonical control and fire its input/change events, so
        // the source reader applies and persists the value exactly as if the
        // setting had been changed in the settings panel.
        const proxyFormat = (format, value) => {
            const number = Number(value);
            switch (format) {
                case "px16": return String(Math.round(number * 16));
                case "decimal1": return number.toFixed(1);
                case "px": return Math.round(number) + " px";
                default: return String(value ?? "");
            }
        };

        const setCanonical = (key, value) => {
            const control = controlFor(key);
            if (!control) return false;
            if (control.type === "checkbox") control.checked = value === true || value === "true";
            else control.value = String(value);
            control.dispatchEvent(new Event("input", { bubbles: true }));
            control.dispatchEvent(new Event("change", { bubbles: true }));
            return true;
        };

        const proxyValues = element => {
            try {
                return JSON.parse(element.dataset.values || "null");
            } catch {
                return null;
            }
        };

        function syncProxies() {
            root.querySelectorAll("[data-reader-proxy],[data-values]").forEach(element => {
                const values = proxyValues(element);
                if (values) {
                    const active = Object.entries(values)
                        .every(([key, value]) => String(settings[key]) === String(value));
                    element.setAttribute("aria-pressed", active ? "true" : "false");
                    return;
                }
                const key = element.dataset.readerProxy;
                const value = settings[key];
                if (value === undefined) return;
                if (element.type === "range") {
                    element.value = String(value);
                } else if (element.type === "checkbox") {
                    element.checked = element.dataset.off !== undefined
                        ? String(value) !== element.dataset.off
                        : Boolean(value);
                } else if (element.dataset.value !== undefined) {
                    const active = String(value) === element.dataset.value;
                    element.setAttribute(
                        element.getAttribute("role") === "radio" ? "aria-checked" : "aria-pressed",
                        active ? "true" : "false");
                }
            });
            root.querySelectorAll("[data-reader-proxy-output]").forEach(output => {
                const key = output.dataset.readerProxyOutput;
                if (settings[key] !== undefined) {
                    output.textContent = proxyFormat(output.dataset.format, settings[key]);
                }
            });
        }

        root.addEventListener("input", event => {
            const element = event.target instanceof Element
                ? event.target.closest("[data-reader-proxy]")
                : null;
            if (!element || element.type !== "range") return;
            const control = controlFor(element.dataset.readerProxy);
            if (!control) return;
            control.value = element.value;
            control.dispatchEvent(new Event("input", { bubbles: true }));
            const output = root.querySelector(
                `[data-reader-proxy-output="${CSS.escape(element.dataset.readerProxy)}"]`);
            if (output) output.textContent = proxyFormat(output.dataset.format, element.value);
        });

        root.addEventListener("change", event => {
            const element = event.target instanceof Element
                ? event.target.closest("[data-reader-proxy]")
                : null;
            if (!element) return;
            const key = element.dataset.readerProxy;
            if (element.type === "range") {
                controlFor(key)?.dispatchEvent(new Event("change", { bubbles: true }));
            } else if (element.type === "checkbox") {
                const value = element.dataset.on !== undefined
                    ? (element.checked ? element.dataset.on : element.dataset.off)
                    : element.checked;
                setCanonical(key, value);
            }
        });

        root.addEventListener("click", event => {
            const element = event.target instanceof Element
                ? event.target.closest("button[data-reader-proxy][data-value],button[data-values]")
                : null;
            if (!element) return;
            const values = proxyValues(element);
            if (values) {
                for (const [key, value] of Object.entries(values)) setCanonical(key, value);
            } else {
                setCanonical(element.dataset.readerProxy, element.dataset.value);
            }
        });

        const setupFrame = () => {
            if (!frame) return;

            menuScrim = document.createElement("div");
            menuScrim.className = "reader-menu-scrim";
            menuScrim.hidden = true;
            menuScrim.addEventListener("click", () => closeMenus(true));
            root.append(menuScrim);

            // Settings: the page opens them at a tab; the <summary> stays hidden.
            const heading = settingsForm?.querySelector(".reader-settings-heading");
            if (heading && !heading.querySelector("[data-reader-settings-close]")) {
                const close = document.createElement("button");
                close.type = "button";
                close.className = "reader-settings-close";
                close.dataset.readerSettingsClose = "";
                close.setAttribute("aria-label", ft("close", "Close"));
                close.title = ft("close", "Close");
                close.textContent = "×";
                close.addEventListener("click", () => {
                    settingsContainer.open = false;
                });
                heading.append(close);
            }

            if (!fullscreenSupported()) {
                root.querySelectorAll("[data-reader-fullscreen-toggle]").forEach(button => {
                    button.hidden = true;
                });
            }
            if (!navigator.share && !navigator.clipboard?.writeText) {
                root.querySelectorAll("[data-reader-share]").forEach(button => {
                    button.hidden = true;
                });
            }

            root.addEventListener("click", event => {
                const target = event.target instanceof Element ? event.target : null;
                if (!target) return;

                if (target.closest("[data-reader-menu-close]")) {
                    closeMenus(true);
                    return;
                }

                const menuToggle = target.closest("[data-reader-menu-toggle]");
                if (menuToggle) {
                    event.preventDefault();
                    openMenuNamed(menuToggle.dataset.readerMenuToggle, menuToggle);
                    return;
                }

                const contentsToggle = target.closest("[data-reader-contents-toggle]");
                if (contentsToggle) {
                    contentsTrigger = contentsToggle;
                    setContents(Boolean(contents?.hidden));
                    return;
                }

                const contentsOpen = target.closest("[data-reader-contents-open]");
                if (contentsOpen) {
                    contentsTrigger = menuTrigger;
                    setContents(true, { tab: contentsOpen.dataset.readerContentsOpen });
                    return;
                }

                if (target.closest("[data-reader-contents-close]") ||
                    target.closest("[data-reader-contents-backdrop]")) {
                    setContents(false);
                    return;
                }

                const tab = target.closest("[data-reader-contents-tab]");
                if (tab) {
                    activateContentsTab(tab.dataset.readerContentsTab);
                    return;
                }

                const settingsOpener = target.closest("[data-reader-settings-open]");
                if (settingsOpener) {
                    openSettings(settingsOpener.dataset.readerSettingsOpen);
                    return;
                }

                if (target.closest("[data-reader-fullscreen-toggle]")) {
                    void toggleFullscreen();
                    closeMenus(false);
                    return;
                }

                if (target.closest("[data-reader-share]")) {
                    void share();
                    closeMenus(false);
                    return;
                }

                const timer = target.closest("[data-reader-timer]");
                if (timer) {
                    setTimer(Number(timer.dataset.readerTimer) || 0);
                    return;
                }

                const step = target.closest("[data-reader-page-step]");
                if (step) {
                    dispatchPage(Number(step.dataset.readerPageStep) || 0);
                    return;
                }

                // Activating a menu item closes its menu (after its own handler ran).
                if (openMenu && openMenu.contains(target) &&
                    target.closest("[role='menuitem'],[role='menuitemradio']")) {
                    closeMenus(true);
                }
            });

            // The shell is the only generic keyboard navigation owner. Renderers
            // consume jularr:reader-page-edge / jularr:reader-seek and never bind
            // a second Arrow/PageUp/PageDown/Space page-turn handler.
            const shortcuts = {
                b: "[data-bookmark-button],[data-reader-bookmark]",
                n: "[data-reader-contents-toggle]",
                "/": '[data-reader-menu-toggle="search"]'
            };
            document.addEventListener("keydown", event => {
                if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
                const target = event.target instanceof Element ? event.target : null;
                if (target?.closest(
                    "input,textarea,select,[contenteditable='true'],[data-reader-menu],[data-reader-contents],[data-reader-settings-container],[role='dialog']")) {
                    return;
                }

                const selector = shortcuts[event.key.toLowerCase()];
                if (selector) {
                    const button = Array.from(root.querySelectorAll(selector)).find(isShown);
                    if (!button) return;
                    event.preventDefault();
                    button.click();
                    return;
                }

                if (readingMode() !== "paged" || overlayOpen()) return;
                const onControl = target?.closest("a,button,summary,[role='button']");
                if (event.key === " " && onControl) return;

                let handled = true;
                if (event.key === "ArrowRight") {
                    dispatchPhysicalPage(1);
                } else if (event.key === "ArrowLeft") {
                    dispatchPhysicalPage(-1);
                } else if (event.key === "PageDown" || (event.key === " " && !event.shiftKey)) {
                    dispatchPage(1);
                } else if (event.key === "PageUp" || (event.key === " " && event.shiftKey)) {
                    dispatchPage(-1);
                } else if (event.key === "Home") {
                    dispatchSeek(Number(progressSlider?.min || 0));
                } else if (event.key === "End") {
                    dispatchSeek(Number(progressSlider?.max || 0));
                } else {
                    handled = false;
                }

                if (handled) event.preventDefault();
            });

            document.addEventListener("pointerdown", event => {
                const target = event.target instanceof Element ? event.target : null;
                if (openMenu && target && !openMenu.contains(target) &&
                    !target.closest("[data-reader-menu-toggle]")) {
                    closeMenus(false);
                }
                if (settingsContainer?.open && target &&
                    !settingsContainer.contains(target) &&
                    !target.closest("[data-reader-settings-open]")) {
                    settingsContainer.open = false;
                }
            }, true);

            root.addEventListener("keydown", event => {
                if (openMenu && openMenu.contains(event.target) &&
                    ["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key) &&
                    !(event.target instanceof HTMLInputElement)) {
                    const items = focusablesIn(openMenu);
                    const index = items.indexOf(document.activeElement);
                    const next = event.key === "Home" ? 0
                        : event.key === "End" ? items.length - 1
                            : (index + (event.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
                    items[next]?.focus();
                    event.preventDefault();
                    return;
                }

                const tab = event.target instanceof Element
                    ? event.target.closest("[data-reader-contents-tab]")
                    : null;
                if (tab && (event.key === "ArrowRight" || event.key === "ArrowLeft")) {
                    const tabs = Array.from(contents.querySelectorAll("[data-reader-contents-tab]"));
                    const index = tabs.indexOf(tab);
                    const next = tabs[(index + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length];
                    activateContentsTab(next.dataset.readerContentsTab, true);
                    event.preventDefault();
                    return;
                }

                // Keyboard users always get the controls back.
                if (event.key === "Tab" && root.classList.contains("reader-chrome-hidden")) showChrome();

                // Keep focus inside a phone sheet while it is modal.
                const contentsModal = contents?.classList.contains("is-overlay") && !contents.hidden;
                const modal = openMenu && compactQuery.matches ? openMenu : contentsModal ? contents : null;
                if (event.key === "Tab" && modal) {
                    const items = focusablesIn(modal);
                    if (!items.length) return;
                    const first = items[0];
                    const last = items.at(-1);
                    if (event.shiftKey && document.activeElement === first) {
                        last.focus();
                        event.preventDefault();
                    } else if (!event.shiftKey && document.activeElement === last) {
                        first.focus();
                        event.preventDefault();
                    }
                }
            });

            if (progressSlider) {
                progressSlider.addEventListener("pointerdown", () => {
                    sliderDragging = true;
                });
                const release = () => {
                    sliderDragging = false;
                };
                progressSlider.addEventListener("pointerup", release);
                progressSlider.addEventListener("pointercancel", release);
                progressSlider.addEventListener("change", release);
                progressSlider.addEventListener("input", () => {
                    paintSlider();
                    root.dispatchEvent(new CustomEvent("jularr:reader-seek", {
                        detail: { value: Number(progressSlider.value) }
                    }));
                });
            }

            root.addEventListener("jularr:reader-location", event => {
                const detail = event.detail || {};
                if (progressSlider) {
                    progressSlider.max = String(Math.max(0, Number(detail.max) || 0));
                    if (!sliderDragging) progressSlider.value = String(Number(detail.value) || 0);
                    if (detail.valueText) progressSlider.setAttribute("aria-valuetext", detail.valueText);
                    paintSlider();
                }
                if (progressText && detail.text != null) progressText.textContent = detail.text;
            });

            document.addEventListener("fullscreenchange", () => {
                syncFullscreen();
                root.dispatchEvent(new CustomEvent("jularr:reader-layout"));
            });
            syncFullscreen();

            inlineContentsQuery.addEventListener?.("change", () => {
                if (contents && !contents.hidden) setContents(false, { focus: false, remember: false });
            });

            let remembered = false;
            try {
                remembered = window.localStorage.getItem(contentsKey) === "1";
            } catch {
            }
            if (remembered && inlineContentsQuery.matches) {
                setContents(true, { focus: false, remember: false });
            }
        };

        // ---- Word details and Learning mode ---------------------------------------
        // _LanguageInspector renders its panel only when instance, profile, permission
        // and content all allow Learning, so the panel being present is the capability check.

        const learningKey = "jularr:reader-learning";

        const setupLearning = () => {
            if (!frame || !detailPanel) return;

            const group = root.querySelector("[data-reader-learning-group]");
            const toggle = root.querySelector("[data-reader-learning-toggle]");
            let enabled = true;
            try {
                enabled = window.sessionStorage.getItem(learningKey) !== "off";
            } catch {
            }

            // Off is ordinary reading: the look-up affordances are hidden by CSS and open details close.
            const applyLearning = next => {
                enabled = next;
                document.documentElement.dataset.readerLearning = enabled ? "on" : "off";
                if (toggle) toggle.checked = enabled;
                if (!enabled) window.JularrLanguageInspector?.close();
                root.dispatchEvent(new CustomEvent("jularr:reader-learning", { detail: { enabled } }));
            };

            if (group) group.hidden = false;
            toggle?.addEventListener("change", () => {
                applyLearning(toggle.checked);
                try {
                    window.sessionStorage.setItem(learningKey, enabled ? "on" : "off");
                } catch {
                }
            });
            applyLearning(enabled);

            // Phones show the details as a bottom sheet that starts compact and can expand.
            const handle = document.createElement("button");
            handle.type = "button";
            handle.className = "reader-detail-handle";
            const syncHandle = expanded => {
                handle.setAttribute("aria-expanded", expanded ? "true" : "false");
                handle.setAttribute(
                    "aria-label",
                    expanded ? ft("detailsCollapse", "Show fewer details") : ft("detailsExpand", "Show more details"));
            };
            syncHandle(false);
            handle.addEventListener("click", () => syncHandle(detailPanel.classList.toggle("is-expanded")));
            detailPanel.prepend(handle);

            new MutationObserver(() => {
                if (!detailPanel.hidden) return;
                detailPanel.classList.remove("is-expanded");
                syncHandle(false);
            }).observe(detailPanel, { attributes: true, attributeFilter: ["hidden"] });
        };

        // ---- Reader states: failures and chapters that are not available offline ---------

        const failureKind = failure => {
            const status = typeof failure === "number" ? failure : Number(failure?.status) || 0;
            if (status === 401 || status === 403) return "permission";
            if (status === 404 || status === 410) return "missing";
            return !navigator.onLine || failure instanceof TypeError ? "network" : "failed";
        };

        const noticeText = kind => {
            switch (kind) {
                case "offline": return ft("stateOffline", "This chapter is not available offline. Connect to the internet or download it first.");
                case "network": return ft("stateNetwork", "The reader could not reach the server. Check the connection and try again.");
                case "permission": return ft("statePermission", "You no longer have access to this content.");
                case "missing": return ft("stateMissing", "This content is not available right now.");
                default: return ft("stateFailed", "Something went wrong. Please try again.");
            }
        };

        const showNotice = (kind, retry = null) => {
            if (!notice) {
                notice = document.createElement("div");
                notice.className = "reader-notice";
                notice.hidden = true;
                const message = document.createElement("p");
                message.dataset.readerNoticeText = "";
                const retryButton = document.createElement("button");
                retryButton.type = "button";
                retryButton.className = "reader-notice-action";
                retryButton.dataset.readerNoticeRetry = "";
                retryButton.textContent = ft("stateRetry", "Retry");
                retryButton.addEventListener("click", () => {
                    const action = noticeRetry;
                    notice.hidden = true;
                    action?.();
                });
                const close = document.createElement("button");
                close.type = "button";
                close.className = "reader-notice-close";
                close.setAttribute("aria-label", ft("close", "Close"));
                close.textContent = "×";
                close.addEventListener("click", () => {
                    notice.hidden = true;
                });
                notice.append(message, retryButton, close);
                root.append(notice);
            }

            noticeRetry = retry;
            notice.setAttribute("role", kind === "offline" ? "status" : "alert");
            notice.querySelector("[data-reader-notice-text]").textContent = noticeText(kind);
            notice.querySelector("[data-reader-notice-retry]").hidden = !retry;
            notice.hidden = false;
            showChrome();
        };

        const setupOfflineNavigation = () => {
            const workId = root.dataset.workId;
            const repository = frame && workId && window.JularrOfflineLibraryRepository
                ? window.JularrOfflineLibraryRepository.forWork(workId)
                : null;
            if (!repository) return;

            const chapterLinks = () => root.querySelectorAll("a[data-book-chapter-link],a[data-novel-chapter-link]");
            const chapterIdOf = link => new URL(link.href, window.location.origin).pathname.split("/").filter(Boolean).pop();

            // While offline, Previous and Next show whether their chapter was downloaded; the link itself stays a link
            // so the offline navigation of the repository can still render a downloaded chapter locally.
            const syncAdjacentChapters = async () => {
                const offline = !navigator.onLine;
                for (const link of chapterLinks()) {
                    let available = true;
                    if (offline) {
                        try {
                            available = (await repository.findLocalChapter(chapterIdOf(link))).available;
                        } catch (error) {
                            console.warn(error);
                            available = false;
                        }
                    }
                    link.dataset.readerTitle ??= link.title;
                    link.title = available ? link.dataset.readerTitle : ft("unavailableOffline", "Not available offline");
                    link.toggleAttribute("data-offline-unavailable", !available);
                    if (available) link.removeAttribute("aria-disabled");
                    else link.setAttribute("aria-disabled", "true");
                }
            };

            window.addEventListener("online", () => void syncAdjacentChapters());
            window.addEventListener("offline", () => void syncAdjacentChapters());
            root.addEventListener("jularr:offline-chapter-missing", event => {
                const chapterId = event.detail?.chapterId;
                showNotice("offline", () => {
                    Array.from(chapterLinks()).find(link => chapterIdOf(link) === chapterId)?.click();
                });
            });
            void syncAdjacentChapters();
        };

        enhanceSettings();
        buildMobileActions();
        buildOverflow();
        setupFrame();
        setupLearning();
        setupOfflineNavigation();
        updateModeVisibility();
        syncProxies();

        // Integration seam for shared Reader extensions such as reader-tts.js.
        // Extensions persist through the same ReaderPreferences command and place
        // actions through the shell's chrome hierarchy. The extension may load
        // before or after this script; whichever runs second performs the mount.
        const api = Object.freeze({
            root,
            surface,
            getSettings: () => settings,
            postSettingsCommand,
            addResetButton,
            updateSourceBadges,
            addOverflowAction,
            addMobileAction,
            isFrame: frame,
            openSettings,
            openContents: tab => setContents(true, { tab }),
            closeContents: () => setContents(false),
            contentsOpen: () => Boolean(contents && !contents.hidden),
            contentsTab: () => activeContentsTab,
            closeMenus,
            showChrome,
            hideChrome,
            toggleChrome,
            toast,
            reportFailure: (failure, retry) => showNotice(failureKind(failure), retry)
        });
        root.readerShell = api;
        window.JularrReaderTts?.mount(api);

        settingsContainer?.addEventListener("toggle", () => {
            if (settingsContainer.open) {
                showChrome();
                setRestoring(false);
            }
        });

        root.addEventListener("jularr:reader-restoring", event => {
            setRestoring(event.detail?.active !== false);
            if (restoring) showChrome();
        });

        root.addEventListener("jularr:reader-mode", event => {
            const mode = event.detail?.readingMode;
            if (mode === "paged" || mode === "continuous") {
                root.dataset.readingMode = mode;
            }

            const direction = event.detail?.pageDirection;
            if (direction === "ltr" || direction === "rtl" || direction === "auto") {
                root.dataset.pageDirection = direction;
            }

            if (typeof event.detail?.immersive === "boolean") {
                root.dataset.readerImmersive = event.detail.immersive ? "true" : "false";
            }

            updateModeVisibility();
        });

        root.addEventListener("jularr:reader-chrome", event => {
            if (event.detail?.visible === true) showChrome();
            else if (event.detail?.visible === false) hideChrome();
            else toggleChrome();
        });

        // An adapter that never reports the end of its restore must not leave the page hidden.
        window.setTimeout(() => setRestoring(false), 2500);

        root.addEventListener("jularr:reader-settings", event => {
            if (event.detail?.settings) {
                settings = event.detail.settings;
                if (settings.readingMode) {
                    root.dataset.readingMode = settings.readingMode;
                }
                updateModeVisibility();
                updateSourceBadges();
                syncProxies();
            }
        });

        root.addEventListener("jularr:reader-settings-response", () => syncProxies());
        root.addEventListener("jularr:reader-proxies", () => syncProxies());

        if (surface) {
            surface.addEventListener("pointerdown", event => {
                if (event.pointerType === "mouse" && event.button !== 0) return;
                if (event.target.closest(interactiveSelector)) return;
                pointerStart = {
                    id: event.pointerId,
                    x: event.clientX,
                    y: event.clientY,
                    time: performance.now()
                };
            }, { passive: true });

            surface.addEventListener("pointerup", event => {
                const start = pointerStart;
                pointerStart = null;
                if (!start || start.id !== event.pointerId) return;
                const dx = event.clientX - start.x;
                const dy = event.clientY - start.y;
                const distance = Math.hypot(dx, dy);
                const selection = window.getSelection();
                if (selection && !selection.isCollapsed && selection.toString().trim()) return;

                if (readingMode() === "paged") {
                    if (Math.abs(dx) > 55 && Math.abs(dx) > Math.abs(dy) * 1.25) {
                        dispatchPhysicalPage(dx < 0 ? 1 : -1);
                        return;
                    }

                    const rect = surface.getBoundingClientRect();
                    const x = event.clientX - rect.left;
                    if (distance < 14 && x < rect.width * .24) {
                        dispatchPhysicalPage(-1);
                        return;
                    }
                    if (distance < 14 && x > rect.width * .76) {
                        dispatchPhysicalPage(1);
                        return;
                    }
                }

                if (distance < 14 && performance.now() - start.time < 700) {
                    toggleChrome();
                }
            }, { passive: true });

            surface.addEventListener("pointercancel", () => {
                pointerStart = null;
            }, { passive: true });
        }

        window.addEventListener("scroll", () => {
            const nextY = window.scrollY;
            const delta = nextY - lastScrollY;
            lastScrollY = nextY;

            if (restoring || overlayOpen()) {
                accumulatedScroll = 0;
                return;
            }

            if (Math.sign(delta) !== Math.sign(accumulatedScroll)) {
                accumulatedScroll = delta;
            } else {
                accumulatedScroll += delta;
            }

            if (accumulatedScroll > 64) {
                hideChrome();
                accumulatedScroll = 0;
            } else if (accumulatedScroll < -40) {
                showChrome();
                accumulatedScroll = 0;
            }
        }, { passive: true });

        document.addEventListener("mousemove", event => {
            if (event.clientY <= 24) showChrome();
        }, { passive: true });

        // Escape closes the deepest layer first: the word details (closed by the inspector itself),
        // then a menu or sheet, a notice, the contents overlay, the settings panel and finally restores hidden chrome.
        document.addEventListener("keydown", event => {
            if (event.key !== "Escape" || event.defaultPrevented || detailOpen()) return;
            if (frame && openMenu) {
                closeMenus(true);
                event.preventDefault();
                return;
            }
            if (frame && notice && !notice.hidden) {
                notice.hidden = true;
                event.preventDefault();
                return;
            }
            if (frame && contents?.classList.contains("is-overlay") && !contents.hidden) {
                setContents(false);
                event.preventDefault();
                return;
            }
            if (settingsContainer?.open) {
                settingsContainer.open = false;
                showChrome();
                event.preventDefault();
                return;
            }
            if (root.classList.contains("reader-chrome-hidden")) showChrome();
        }, true);

        window.addEventListener("load", () => {
            window.setTimeout(() => {
                setRestoring(false);
                root.dataset.readerReady = "true";
                showChrome();
            }, 250);
        }, { once: true });

        // Scripts are normally loaded after DOMContentLoaded; do not wait forever
        // if the load event already fired.
        if (document.readyState === "complete") {
            window.setTimeout(() => {
                setRestoring(false);
                root.dataset.readerReady = "true";
                showChrome();
            }, 50);
        }
    }
})();
