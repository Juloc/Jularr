// The player's own controls. episode-player.js owns sources, the (absolute) timeline, subtitles
// and progress; this file only drives what the native <video controls> used to: play/pause,
// volume, full screen, picture-in-picture, the settings menu, auto-hiding the chrome and taps on
// the video (show/hide, double-tap seek). Every control exists once; there is deliberately no
// second timeline and no native control bar.
(() => {
    const root = document.querySelector("[data-episode-player]");
    const stage = root?.querySelector("[data-player-chrome]");
    const video = root?.querySelector("[data-playback-video]");
    if (!root || !stage || !video) return;

    const design = window.JularrPlayerDesign;
    // Alternate icon shapes come from design/player/player-icons.json via the page, never copied here.
    const icons = (() => {
        try {
            return JSON.parse(root.querySelector("[data-player-icons]")?.textContent || "{}");
        } catch {
            return {};
        }
    })();
    const text = (() => {
        try {
            return JSON.parse(root.querySelector("[data-player-text]")?.textContent || "{}");
        } catch {
            return {};
        }
    })();
    const settings = stage.querySelector("[data-chrome-settings]");
    const timeline = stage.querySelector("[data-playback-timeline]");
    const volumeSlider = stage.querySelector("[data-chrome-volume]");
    const subtitleSelect = stage.querySelector("[data-subtitle-track]");
    const subtitleControl = stage.querySelector('[data-chrome-open-setting="subtitles"]');
    // One picture-in-picture button sits in the top chrome of a phone or tablet stage and one in the bar of a wide stage; the layout shows one.
    const pipButtons = stage.querySelectorAll("[data-chrome-pip]");
    const volumeKey = "jularr.player.volume";
    const hideDelayMs = 3000;
    let hideTimer = 0;
    let lastSubtitleChoice = null;

    const setIcon = (button, name) => {
        const path = button?.querySelector("svg path");
        const icon = icons[name];
        if (path && icon) {
            path.setAttribute("d", icon.path);
            path.parentElement.setAttribute("viewBox", icon.viewBox);
        }
    };

    // --- chrome visibility ------------------------------------------------------------------
    // Controls stay up while paused and hide a few seconds into playback without interaction.
    const settingsOpen = () => settings && !settings.hidden;
    const chromeHidden = () => stage.dataset.chromeState === "hidden";
    const hide = () => {
        window.clearTimeout(hideTimer);
        if (!settingsOpen()) stage.dataset.chromeState = "hidden";
    };
    const show = () => {
        stage.dataset.chromeState = "visible";
        window.clearTimeout(hideTimer);
        if (!video.paused && !settingsOpen()) {
            hideTimer = window.setTimeout(() => {
                if (!video.paused && !settingsOpen() && !stage.contains(document.activeElement?.closest?.(".player-settings"))) {
                    hide();
                }
            }, hideDelayMs);
        }
    };
    // Only a real mouse reveals the controls by moving; touch devices use the tap below.
    stage.addEventListener("pointermove", event => {
        if (event.pointerType === "mouse") show();
    });
    stage.addEventListener("pointerleave", event => {
        if (event.pointerType === "mouse" && !video.paused && !settingsOpen()) hide();
    });
    // Keyboard focus shows the controls; the focus a click gives the stage does not.
    stage.addEventListener("focusin", event => {
        if (event.target.matches?.(":focus-visible")) show();
    });

    // --- play / pause ----------------------------------------------------------------------
    const togglePlay = () => {
        if (video.hidden) return;
        if (video.paused || video.ended) {
            void video.play().catch(() => {});
        } else {
            video.pause();
        }
    };
    const renderPlayState = () => {
        const playing = !video.paused && !video.ended;
        stage.dataset.playing = String(playing);
        for (const button of stage.querySelectorAll("[data-chrome-play]")) {
            setIcon(button, playing ? "pause" : "play");
            button.setAttribute("aria-label", playing ? button.dataset.labelPause : button.dataset.labelPlay);
        }
        show();
    };
    for (const button of stage.querySelectorAll("[data-chrome-play]")) {
        button.addEventListener("click", togglePlay);
    }
    root.addEventListener(design?.actionEvent || "jularr:player-action", event => {
        if (event.detail?.action === "playPause") togglePlay();
    });
    for (const name of ["play", "pause", "ended", "emptied"]) video.addEventListener(name, renderPlayState);

    // --- taps on the video (touch and mouse alike) --------------------------------------------
    // A tap shows or hides the controls and never pauses. Double tap left/right seeks, repeated
    // taps add up; double tap in the middle toggles full screen (player-gestures.js decides).
    const seekSeconds = design.seekSeconds(root);
    const taps = window.JularrPlayerGestures?.createTapDecider({ backSeconds: seekSeconds.back, forwardSeconds: seekSeconds.forward });
    const interactive = ".player-center button, .player-bottom, .player-settings, .player-learning-sheet, .post-play, " +
        ".player-error, .player-subtitle-bubble, .playback-preparation-actions, button, a, input, select, textarea, label, summary";
    const isSurface = target => target instanceof Element && !target.closest(interactive);

    const feedbackTimers = {};
    const showSeekFeedback = (zone, total) => {
        const element = stage.querySelector(`[data-seek-feedback="${zone}"]`);
        const label = element?.querySelector("[data-seek-feedback-label]");
        if (!element || !label) return;
        const template = text[zone === "back" ? "playback.gesture.seekBack" : "playback.gesture.seekForward"] || "{seconds}";
        label.textContent = template.replace("{seconds}", String(total));
        element.hidden = false;
        // Restart the ripple on every tap of the series.
        element.classList.remove("is-rippling");
        void element.offsetWidth;
        element.classList.add("is-rippling");
        window.clearTimeout(feedbackTimers[zone]);
        feedbackTimers[zone] = window.setTimeout(() => {
            element.hidden = true;
            element.classList.remove("is-rippling");
        }, 800);
    };

    const handleTap = fraction => {
        if (!taps) {
            if (chromeHidden()) show(); else hide();
            return;
        }

        const now = performance.now();
        const decision = taps.tap(fraction, now);
        if (decision.action === "wait") {
            window.setTimeout(() => {
                if (taps.settle(performance.now()).action === "toggleControls") {
                    if (chromeHidden()) show(); else hide();
                }
            }, taps.delayMs + 20);
        } else if (decision.action === "seek") {
            design?.dispatch(root, decision.zone === "back" ? "seekBack10" : "seekForward10");
            showSeekFeedback(decision.zone, decision.total);
        } else if (decision.action === "doubleTapCenter") {
            void presentation.toggleFullscreen();
        }
    };

    let press = null;
    stage.addEventListener("pointerdown", event => {
        press = null;
        if (!event.isPrimary || event.button > 0) return;
        if (!isSurface(event.target)) {
            // Using a control keeps the controls up.
            show();
            return;
        }

        // A tap next to an open settings menu only closes it.
        if (settingsOpen()) {
            setSettings(false);
            return;
        }

        press = { x: event.clientX, y: event.clientY, at: performance.now() };
    });
    stage.addEventListener("pointercancel", () => { press = null; });
    stage.addEventListener("pointerup", event => {
        const start = press;
        press = null;
        if (!start || !event.isPrimary || video.hidden || !isSurface(event.target)) return;
        // Drags, swipes and long presses are not taps.
        if (Math.hypot(event.clientX - start.x, event.clientY - start.y) > 16 || performance.now() - start.at > 600) return;
        const rect = stage.getBoundingClientRect();
        handleTap(rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0.5);
    });
    // Double-click on the video would select text or zoom; the tap logic above owns it.
    stage.addEventListener("dblclick", event => {
        if (isSurface(event.target)) event.preventDefault();
    });

    // --- timeline fill (the value itself is written by episode-player.js) -------------------
    const renderTimelineFill = () => {
        const max = Number(timeline?.max) || 0;
        const value = Number(timeline?.value) || 0;
        timeline?.style.setProperty("--progress", max > 0 ? `${(value / max) * 100}%` : "0%");
    };
    timeline?.addEventListener("input", renderTimelineFill);
    video.addEventListener("timeupdate", renderTimelineFill);
    video.addEventListener("seeked", renderTimelineFill);
    video.addEventListener("loadedmetadata", renderTimelineFill);

    // --- volume ----------------------------------------------------------------------------
    const muteButton = stage.querySelector("[data-chrome-mute]");
    const renderVolume = () => {
        const muted = video.muted || video.volume === 0;
        setIcon(muteButton, muted ? "volumeMuted" : "volume");
        muteButton?.setAttribute("aria-label", muted ? muteButton.dataset.labelUnmute : muteButton.dataset.labelMute);
        if (volumeSlider) {
            volumeSlider.value = String(video.muted ? 0 : video.volume);
            volumeSlider.style.setProperty("--progress", `${(video.muted ? 0 : video.volume) * 100}%`);
        }
    };
    try {
        const stored = Number(localStorage.getItem(volumeKey));
        if (Number.isFinite(stored) && stored >= 0 && stored <= 1 && localStorage.getItem(volumeKey) !== null) video.volume = stored;
    } catch { /* storage unavailable */ }
    muteButton?.addEventListener("click", () => {
        if (video.muted || video.volume === 0) {
            video.muted = false;
            if (video.volume === 0) video.volume = 0.6;
        } else {
            video.muted = true;
        }
    });
    volumeSlider?.addEventListener("input", () => {
        video.volume = Number(volumeSlider.value);
        video.muted = video.volume === 0;
    });
    video.addEventListener("volumechange", () => {
        renderVolume();
        try { localStorage.setItem(volumeKey, String(video.volume)); } catch { /* storage unavailable */ }
    });
    renderVolume();

    // --- presentation: inline, Theater, element / system full screen, picture-in-picture --------------------
    // player-presentation.js owns the modes and the one resolver; this block only renders its state. The
    // capability snapshot is probed once here and downgraded by the resolver when a real call fails.
    const presentationModes = window.JularrPlayerPresentation.modes;
    const systemPlayerGroup = stage.querySelector("[data-chrome-system-player-group]");
    const renderPresentation = state => {
        for (const button of stage.querySelectorAll("[data-chrome-fullscreen]")) {
            setIcon(button, state.immersive ? "fullscreenExit" : "fullscreen");
            button.setAttribute("aria-label", state.immersive ? button.dataset.labelExit : button.dataset.labelEnter);
            button.title = button.getAttribute("aria-label");
        }

        for (const pipButton of pipButtons) {
            pipButton.hidden = !state.supports.pictureInPicture;
            pipButton.setAttribute("aria-pressed", String(state.mode === presentationModes.pictureInPicture));
        }

        if (systemPlayerGroup) systemPlayerGroup.hidden = !state.supports.nativeFullscreen;
        if (state.mode !== state.previousMode) {
            root.dispatchEvent(new CustomEvent(window.JularrPlayerPresentation.changeEvent, {
                bubbles: true,
                detail: { mode: state.mode, previousMode: state.previousMode }
            }));
            // Back in a Jularr-owned mode the controls come up so the user is not left with a bare picture.
            show();
        }
    };
    const presentation = window.JularrPlayerPresentation.create({
        win: window,
        stage,
        video,
        capabilities: window.JularrPlaybackCapabilities.probePresentation(document, stage, video),
        onUpdate: renderPresentation,
        // Back and Escape close the deepest panel (learning sheet above the settings menu) before Theater itself.
        closePanels: () => {
            const sheet = stage.querySelector("[data-word-inspector]");
            if (sheet && !sheet.hidden) {
                design.dispatch(root, design.actions.closeOverlay);
                return true;
            }

            if (!settingsOpen()) return false;
            setSettings(false);
            return true;
        }
    });
    for (const button of stage.querySelectorAll("[data-chrome-fullscreen]")) {
        button.addEventListener("click", () => void presentation.toggleFullscreen());
    }

    for (const pipButton of pipButtons) {
        pipButton.addEventListener("click", () => void presentation.togglePictureInPicture());
    }
    stage.querySelector("[data-chrome-system-player]")?.addEventListener("click", () => {
        setSettings(false);
        presentation.toggleNativeFullscreen();
    });

    // --- settings panel ---------------------------------------------------------------------------
    // The gear opens the whole panel. Subtitles, Audio, Quality and Speed on the bar open the same panel showing only that choice as
    // a list of options; the options are read from the select of the panel, so choosing one is the change the select already handles.
    const settingsToggles = stage.querySelectorAll("[data-chrome-settings-toggle], [data-chrome-open-setting]");
    const settingsTitle = settings?.querySelector("[data-settings-title]");
    const settingRows = settings ? [...settings.querySelectorAll("[data-setting-row]")] : [];
    const settingSelects = {
        subtitles: subtitleSelect,
        audio: stage.querySelector("[data-audio-track]"),
        quality: stage.querySelector("[data-quality-cap]"),
        speed: stage.querySelector("[data-playback-speed]")
    };
    let optionList = null;
    let settingsOpener = null;

    const optionLabel = option => option.textContent.replace(/\s+/g, " ").trim();
    const renderSettingValues = () => {
        for (const control of stage.querySelectorAll("[data-chrome-open-setting]")) {
            const value = control.querySelector("[data-chrome-setting-value]");
            const option = settingSelects[control.dataset.chromeOpenSetting]?.selectedOptions[0];
            if (value && option) value.textContent = optionLabel(option);
        }
    };

    const renderOptions = (mode, title) => {
        optionList?.remove();
        optionList = null;
        const select = settingSelects[mode];
        if (!select) return;
        optionList = document.createElement("div");
        optionList.className = "player-options";
        optionList.setAttribute("role", "radiogroup");
        optionList.setAttribute("aria-label", title);
        for (const option of select.options) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "player-option";
            button.setAttribute("role", "radio");
            button.setAttribute("aria-checked", String(option.value === select.value));
            if (option.disabled) button.setAttribute("aria-disabled", "true");
            button.dataset.value = option.value;
            button.textContent = optionLabel(option);
            optionList.append(button);
        }
        settings.querySelector(".player-settings-header").after(optionList);
    };

    const setSettings = (open, mode = "all") => {
        if (!settings) return;
        settings.hidden = !open;
        for (const toggle of settingsToggles) {
            toggle.setAttribute("aria-expanded", String(open && (toggle.dataset.chromeOpenSetting || "all") === mode));
        }

        if (!open) {
            // Focus goes back to the control that opened the panel instead of being lost with it.
            const hadFocus = settings.contains(document.activeElement);
            optionList?.remove();
            optionList = null;
            if (hadFocus) settingsOpener?.focus({ preventScroll: true });
            show();
            return;
        }

        const single = mode !== "all";
        const title = single
            ? stage.querySelector(`[data-chrome-open-setting="${mode}"] .player-control-label`)?.textContent || ""
            : settingsTitle?.dataset.titleAll || "";
        settings.dataset.settingsMode = mode;
        if (settingsTitle) settingsTitle.textContent = title;
        // In the single-choice view the select row is replaced by the option list; its hint (burn-in, quality limit) stays.
        for (const row of settingRows) {
            row.toggleAttribute("data-filtered", single && (row.dataset.settingRow !== mode || row.classList.contains("player-setting")));
        }
        renderOptions(single ? mode : "", title);
        stage.dataset.chromeState = "visible";
        window.clearTimeout(hideTimer);
        const first = optionList
            ? optionList.querySelector('[aria-checked="true"]') || optionList.querySelector(".player-option")
            : settings.querySelector("select, input, button:not([data-chrome-settings-close])");
        first?.focus({ preventScroll: true });
    };

    for (const toggle of settingsToggles) {
        toggle.addEventListener("click", () => {
            const mode = toggle.dataset.chromeOpenSetting || "all";
            if (!settings.hidden && settings.dataset.settingsMode === mode) {
                setSettings(false);
                return;
            }

            settingsOpener = toggle;
            setSettings(true, mode);
        });
    }
    stage.querySelector("[data-chrome-settings-close]")?.addEventListener("click", () => setSettings(false));
    settings?.addEventListener("click", event => {
        const button = event.target.closest(".player-option");
        const select = settingSelects[settings.dataset.settingsMode];
        if (!button || !select || button.getAttribute("aria-disabled") === "true") return;
        select.value = button.dataset.value;
        select.dispatchEvent(new Event("change", { bubbles: true }));
        setSettings(false);
    });
    settings?.addEventListener("keydown", event => {
        if (!optionList || (event.key !== "ArrowDown" && event.key !== "ArrowUp")) return;
        const options = [...optionList.querySelectorAll('.player-option:not([aria-disabled="true"])')];
        const at = options.indexOf(document.activeElement);
        if (at < 0) return;
        event.preventDefault();
        options[(at + (event.key === "ArrowDown" ? 1 : options.length - 1)) % options.length].focus();
    });
    for (const select of Object.values(settingSelects)) select?.addEventListener("change", renderSettingValues);
    video.addEventListener("loadedmetadata", renderSettingValues);
    renderSettingValues();
    // Controls outside the panel (other than the ones that open it) close it too.
    stage.addEventListener("pointerdown", event => {
        if (settingsOpen() && !isSurface(event.target) && !event.target.closest(".player-settings, [data-chrome-settings-toggle], [data-chrome-open-setting]")) {
            setSettings(false);
        }
    });

    // --- subtitle shortcut (the C key) ------------------------------------------------------------
    const renderSubtitleControl = () => {
        if (subtitleControl) subtitleControl.hidden = !subtitleSelect || subtitleSelect.options.length < 2;
        if (subtitleSelect && subtitleSelect.value !== "off") lastSubtitleChoice = subtitleSelect.value;
    };
    const toggleSubtitles = () => {
        if (!subtitleSelect) return;
        const enabled = [...subtitleSelect.options].filter(option => option.value !== "off" && !option.disabled);
        const next = subtitleSelect.value !== "off"
            ? "off"
            : (lastSubtitleChoice && enabled.some(option => option.value === lastSubtitleChoice)
                ? lastSubtitleChoice
                // Prefer a text track: a picture track restarts the stream for a burn-in.
                : (enabled.find(option => option.dataset.image !== "true") || enabled[0])?.value);
        if (!next) return;
        subtitleSelect.value = next;
        subtitleSelect.dispatchEvent(new Event("change", { bubbles: true }));
    };
    subtitleSelect?.addEventListener("change", renderSubtitleControl);
    renderSubtitleControl();

    // --- keyboard (when focus is in the player and not in a form control) -------------------
    stage.addEventListener("keydown", event => {
        if (event.target.closest("select, input:not([type=range]), textarea, .player-settings")) {
            if (event.key === "Escape" && settingsOpen()) {
                event.preventDefault();
                setSettings(false);
            }
            return;
        }
        // Space on a focused button or link activates it instead of toggling playback.
        if (event.key === " " && event.target.closest("button, a[href]")) return;
        const dispatch = action => design ? design.dispatch(root, action) : null;
        switch (event.key) {
            case " ":
            case "k":
                event.preventDefault();
                togglePlay();
                break;
            case "ArrowLeft":
            case "j":
                event.preventDefault();
                dispatch("seekBack10");
                break;
            case "ArrowRight":
            case "l":
                event.preventDefault();
                dispatch("seekForward10");
                break;
            case "ArrowUp":
                event.preventDefault();
                video.volume = Math.min(1, video.volume + 0.05);
                video.muted = false;
                break;
            case "ArrowDown":
                event.preventDefault();
                video.volume = Math.max(0, video.volume - 0.05);
                break;
            case "m":
                video.muted = !video.muted;
                break;
            case "f":
                void presentation.toggleFullscreen();
                break;
            case "c":
                toggleSubtitles();
                break;
            case "Escape":
                // Closing the menu uses the key; the next Escape leaves Theater (player-presentation.js).
                if (settingsOpen()) {
                    event.preventDefault();
                    setSettings(false);
                }
                break;
        }
        show();
    });

    renderPlayState();
    renderTimelineFill();
    const initialPresentation = presentation.state();
    renderPresentation({ ...initialPresentation, previousMode: initialPresentation.mode });
})();

// Season filter for the episode list next to the player, and the owner's sources dialog.
(() => {
    const select = document.querySelector("[data-watch-season]");
    select?.addEventListener("change", () => {
        for (const item of document.querySelectorAll("[data-watch-episodes] li[data-season]")) {
            item.hidden = item.dataset.season !== select.value;
        }
    });

    for (const opener of document.querySelectorAll("[data-open-dialog]")) {
        const dialog = document.getElementById(opener.dataset.openDialog);
        opener.addEventListener("click", () => dialog?.showModal());
    }
    const autoOpen = document.querySelector("dialog[data-open-on-load='true']");
    autoOpen?.showModal();
})();
