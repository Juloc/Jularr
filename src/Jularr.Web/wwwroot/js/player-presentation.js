// Presentation of the one player stage (docs/mockups/player/SPEC.md, iOS / iPadOS WebKit path).
// Inline, Theater, ElementFullscreen, NativeFullscreen and PictureInPicture are client-only views of the same
// <video> and the same ActiveSession; nothing here touches sources, tracks, speed or progress. The mode is derived
// from what the platform reports (fullscreen and picture-in-picture state, WebKit presentation mode) plus the one
// flag this module owns (Theater), so it cannot drift from the real surface. The capability snapshot comes from
// JularrPlaybackCapabilities.probePresentation and is downgraded here when a real call fails.
(() => {
    const modes = Object.freeze({
        inline: "inline",
        theater: "theater",
        elementFullscreen: "element-fullscreen",
        nativeFullscreen: "native-fullscreen",
        pictureInPicture: "picture-in-picture"
    });
    const changeEvent = "jularr:player-presentation";
    // Rejections that only mean "not now" (no user gesture, metadata not loaded yet, superseded request): they
    // never downgrade a capability, any other failure does.
    const transientErrors = new Set(["NotAllowedError", "InvalidStateError", "AbortError"]);
    const historyMarker = "jularrPlayerTheater";

    // closePanels() closes the deepest open menu or sheet and reports whether it did; Escape and the back gesture
    // use it before they leave Theater.
    const create = ({ win, stage, video, capabilities, onUpdate, closePanels }) => {
        const doc = win.document;
        const panel = stage.parentElement;
        const supported = {
            elementFullscreen: capabilities.elementFullscreen,
            nativeFullscreen: capabilities.nativeFullscreen,
            pictureInPicture: [...capabilities.pictureInPicture]
        };
        let theater = false;
        // The history entry that gives Theater its back gesture, and a back() of ours whose popstate is still on its way.
        let historyEntry = false;
        let pendingBack = false;
        let savedScroll = null;
        let mode = modes.inline;
        stage.dataset.presentation = mode;

        const elementFullscreenActive = () => (doc.fullscreenElement || doc.webkitFullscreenElement) === stage;
        const pictureInPictureActive = () => doc.pictureInPictureElement === video || video.webkitPresentationMode === "picture-in-picture";
        const nativeFullscreenActive = () => video.webkitPresentationMode === "fullscreen" || video.webkitDisplayingFullscreen === true;

        const resolveMode = () => {
            if (pictureInPictureActive()) return modes.pictureInPicture;
            if (nativeFullscreenActive()) return modes.nativeFullscreen;
            if (elementFullscreenActive()) return modes.elementFullscreen;
            return theater ? modes.theater : modes.inline;
        };

        const state = () => ({
            mode,
            // The Fullscreen button reflects the Jularr-owned full-screen modes even while the video is popped out.
            immersive: theater || elementFullscreenActive(),
            supports: { pictureInPicture: supported.pictureInPicture.length > 0, nativeFullscreen: supported.nativeFullscreen }
        });

        const sync = () => {
            const previousMode = mode;
            mode = resolveMode();
            stage.dataset.presentation = mode;
            // The page scroll lock follows the surface that really is Theater: not while the video is popped out.
            if (mode === modes.theater) doc.documentElement.dataset.playerTheater = "true";
            else delete doc.documentElement.dataset.playerTheater;
            onUpdate?.({ ...state(), previousMode });
        };

        const report = (what, error) => win.console?.warn?.(`Jularr player: ${what} failed (${error?.name || "error"}).`);

        // --- Theater: the same stage fixed over the viewport (CSS keys off data-presentation) ---------------
        // The back gesture leaves Theater through one marker entry on top of the page entry. Every exit removes it
        // again with back() (async: pendingBack swallows that popstate), so no route needs an extra Back press. A
        // page left while the marker is on top (a link, autoplay, reload) is cleaned up when it is loaded again.
        const pushMarker = () => {
            try {
                win.history.pushState({ [historyMarker]: true }, "");
                historyEntry = true;
            } catch (error) {
                report("history entry", error);
            }
        };

        const enterTheater = () => {
            if (theater) return;
            theater = true;
            savedScroll = { x: win.scrollX, y: win.scrollY };
            // The stage leaves the page flow; the panel keeps its height so the page behind does not jump.
            panel.style.minHeight = `${stage.offsetHeight}px`;
            // A back() still in flight would pop a fresh entry: the marker is pushed when it has arrived.
            if (!pendingBack) pushMarker();
            sync();
        };

        const leaveTheater = () => {
            if (!theater) return;
            theater = false;
            panel.style.minHeight = "";
            if (savedScroll && (win.scrollX !== savedScroll.x || win.scrollY !== savedScroll.y)) {
                win.scrollTo(savedScroll.x, savedScroll.y);
            }

            savedScroll = null;
            sync();
            if (historyEntry) {
                historyEntry = false;
                pendingBack = true;
                win.history.back();
            }
        };

        // --- the normal Fullscreen action: element fullscreen when it works, otherwise Theater ---------------
        const enterFullscreen = async () => {
            if (supported.elementFullscreen === "standard") {
                try {
                    await stage.requestFullscreen();
                    if (elementFullscreenActive()) return;
                    supported.elementFullscreen = null;
                } catch (error) {
                    if (!transientErrors.has(error?.name)) supported.elementFullscreen = null;
                    report("fullscreen", error);
                }
            } else if (supported.elementFullscreen === "webkit") {
                try {
                    stage.webkitRequestFullscreen();
                    return;
                } catch (error) {
                    if (!transientErrors.has(error?.name)) supported.elementFullscreen = null;
                    report("fullscreen", error);
                }
            }

            // Never the native Apple player here: it would drop the Jularr controls and overlays.
            enterTheater();
        };

        const exitElementFullscreen = async () => {
            try {
                if (typeof doc.exitFullscreen === "function") await doc.exitFullscreen();
                else doc.webkitExitFullscreen();
            } catch (error) {
                report("leaving fullscreen", error);
            }
        };

        const toggleFullscreen = async () => {
            if (elementFullscreenActive()) {
                await exitElementFullscreen();
            } else if (theater) {
                leaveTheater();
            } else {
                await enterFullscreen();
            }
        };

        // --- explicit system player (WebKit's own video fullscreen) -----------------------------------------
        const toggleNativeFullscreen = () => {
            try {
                if (nativeFullscreenActive()) video.webkitExitFullscreen();
                else if (supported.nativeFullscreen) video.webkitEnterFullscreen();
            } catch (error) {
                if (!transientErrors.has(error?.name)) supported.nativeFullscreen = false;
                report("system player", error);
                sync();
            }
        };

        // --- picture-in-picture: standard API first, WebKit presentation mode second ------------------------
        const togglePictureInPicture = async () => {
            if (pictureInPictureActive()) {
                try {
                    if (doc.pictureInPictureElement === video) await doc.exitPictureInPicture();
                    else video.webkitSetPresentationMode("inline");
                } catch (error) {
                    report("leaving picture-in-picture", error);
                }

                return;
            }

            while (supported.pictureInPicture.length > 0) {
                try {
                    if (supported.pictureInPicture[0] === "standard") await video.requestPictureInPicture();
                    else video.webkitSetPresentationMode("picture-in-picture");
                    return;
                } catch (error) {
                    report("picture-in-picture", error);
                    if (transientErrors.has(error?.name)) return;
                    // A route that reported support but failed for real is not offered again until the page reloads.
                    supported.pictureInPicture.shift();
                }
            }

            sync();
        };

        for (const name of ["fullscreenchange", "webkitfullscreenchange"]) doc.addEventListener(name, sync);
        for (const name of ["enterpictureinpicture", "leavepictureinpicture", "webkitpresentationmodechanged", "webkitbeginfullscreen", "webkitendfullscreen"]) {
            video.addEventListener(name, sync);
        }

        doc.addEventListener("keydown", event => {
            // A menu or other handler that already used Escape keeps it.
            if (event.key !== "Escape" || !theater || event.defaultPrevented || elementFullscreenActive()) return;
            if (!closePanels?.()) leaveTheater();
        });
        win.addEventListener("popstate", () => {
            if (pendingBack) {
                pendingBack = false;
                if (theater && !historyEntry) pushMarker();
                return;
            }

            if (!theater || !historyEntry) return;
            // The user's own back gesture already removed the marker; open panels close first and Theater stays.
            historyEntry = false;
            if (closePanels?.()) pushMarker();
            else leaveTheater();
        });

        // A marker entry that outlived its page: leave it behind in one step instead of costing a Back press.
        if (win.history.state?.[historyMarker]) win.history.back();

        return Object.freeze({ state, toggleFullscreen, toggleNativeFullscreen, togglePictureInPicture });
    };

    window.JularrPlayerPresentation = Object.freeze({ modes, changeEvent, create });
})();
