(() => {
    const ACTION_EVENT = "jularr:player-action";
    // seekBack10/seekForward10 are the stable cross-client command ids (companion, TV, session hub); every
    // web consumer seeks by the canonical back/forward increments from seekSeconds(), never by the id's number.
    const actions = Object.freeze({
        playPause: "playPause",
        seekBack10: "seekBack10",
        seekForward10: "seekForward10",
        seekTo: "seekTo",
        selectAudioTrack: "selectAudioTrack",
        selectSubtitleTrack: "selectSubtitleTrack",
        repeatCurrentCue: "repeatCurrentCue",
        learnCurrentCue: "learnCurrentCue",
        openWord: "openWord",
        markKnown: "markKnown",
        addToLearning: "addToLearning",
        openCompanion: "openCompanion",
        closeOverlay: "closeOverlay",
        exitPlayer: "exitPlayer"
    });

    // The manual seek increments of design/player/player-tokens.json (back 10 s, forward 30 s). The server renders
    // them on the player root so the chrome, the player and the media session read one asymmetric pair.
    const seekSeconds = root => {
        const back = Number(root.dataset.seekBackSeconds);
        const forward = Number(root.dataset.seekForwardSeconds);
        if (!(back > 0) || !(forward > 0)) {
            throw new Error("The player root must declare data-seek-back-seconds and data-seek-forward-seconds.");
        }

        return Object.freeze({ back, forward });
    };

    const dispatch = (root, action, detail = {}) => {
        root.dispatchEvent(new CustomEvent(ACTION_EVENT, {
            bubbles: true,
            detail: { action, ...detail }
        }));
    };

    const cueText = cue =>
        (cue?.tokens || []).map(token => token.surface || "").join("");

    // Cue lookup always runs on the media clock (milliseconds of media time),
    // never on wall-clock time, so playback speed cannot shift subtitle timing.
    // `cues` must be sorted by startMs.
    const cueIndexAt = (cues, timeMs) => {
        let low = 0;
        let high = cues.length - 1;
        let candidate = -1;

        while (low <= high) {
            const middle = Math.floor((low + high) / 2);
            if (cues[middle].startMs <= timeMs) {
                candidate = middle;
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        return candidate >= 0 && timeMs <= cues[candidate].endMs ? candidate : -1;
    };

    // Plain playback subtitles may overlap (for example signs plus dialogue).
    const activeCuesAt = (cues, timeMs) =>
        cues.filter(cue => cue.startMs <= timeMs && timeMs <= cue.endMs);

    // Start of the line to repeat: the active cue, otherwise the latest cue
    // that already started; null when no line has started yet.
    const lineStartAt = (cues, timeMs) => {
        let start = null;
        for (const cue of cues) {
            if (cue.startMs > timeMs) {
                break;
            }
            start = cue.startMs;
        }
        return start;
    };

    const containsJapanese = value =>
        [...(value || "")].some(character => {
            const code = character.codePointAt(0);
            return (code >= 0x3040 && code <= 0x30ff) ||
                (code >= 0x3400 && code <= 0x4dbf) ||
                (code >= 0x4e00 && code <= 0x9fff) ||
                character === "々" || character === "〆" || character === "ヶ";
        });

    const isClickableToken = token =>
        token.isInteractive === true ||
        token.isVocabulary === true ||
        containsJapanese(token.surface);

    const tokenLabel = token => {
        const bits = [token.surface];
        if (token.reading) bits.push(token.reading);
        if (token.meaning) bits.push(token.meaning);
        return bits.filter(Boolean).join(" · ");
    };

    const renderCue = (root, overlay, cue) => {
        overlay.replaceChildren();

        if (!cue) {
            overlay.hidden = true;
            overlay.removeAttribute("data-active-cue");
            return;
        }

        overlay.hidden = false;
        overlay.dataset.activeCue = String(cue.startMs ?? "");
        overlay.setAttribute("aria-label", `Japanese subtitle: ${cueText(cue)}. Tap the line to learn it.`);

        const bubble = document.createElement("div");
        bubble.className = "player-subtitle-bubble";
        bubble.dataset.playerAction = actions.learnCurrentCue;
        bubble.setAttribute("role", "group");
        bubble.setAttribute("aria-label", "Interactive Japanese subtitle");

        for (const token of cue.tokens || []) {
            if (isClickableToken(token)) {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "player-subtitle-token";
                button.dataset.playerAction = actions.openWord;
                button.textContent = token.surface;
                button.title = tokenLabel(token);
                button.setAttribute("aria-label", tokenLabel(token));
                button.addEventListener("click", event => {
                    event.stopPropagation();
                    dispatch(root, actions.openWord, { token, cue, element: button });
                });
                bubble.append(button);
            } else {
                const span = document.createElement("span");
                span.textContent = token.surface;
                bubble.append(span);
            }
        }

        const learn = () => dispatch(root, actions.learnCurrentCue, { cue });
        bubble.addEventListener("click", event => {
            if (!(event.target instanceof HTMLButtonElement)) {
                learn();
            }
        });

        const lineButton = document.createElement("button");
        lineButton.type = "button";
        lineButton.className = "player-subtitle-line-action";
        lineButton.dataset.playerAction = actions.learnCurrentCue;
        lineButton.textContent = "Learn";
        lineButton.setAttribute("aria-label", "Learn this subtitle line");
        lineButton.addEventListener("click", event => {
            event.stopPropagation();
            learn();
        });
        bubble.append(lineButton);

        overlay.append(bubble);
    };

    window.JularrPlayerDesign = Object.freeze({
        actionEvent: ACTION_EVENT,
        actions,
        seekSeconds,
        dispatch,
        cueText,
        cueIndexAt,
        activeCuesAt,
        lineStartAt,
        isClickableToken,
        renderCue
    });
})();
