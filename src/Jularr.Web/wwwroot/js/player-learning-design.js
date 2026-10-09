(() => {
    const design = window.JularrPlayerDesign;
    if (!design) return;
    const { actions, dispatch, cueText } = design;

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

    const attachInspector = ({
        root, overlay, video, design, inspector, learningKicker, word, reading,
        meaning, state, replay, closeLearning, getCues, getActiveIndex, renderActiveCue
    }) => {
        if (!root || !overlay) return null;
        const learningTools = Boolean(
            inspector && learningKicker && word && reading && meaning && state &&
            replay && closeLearning);
        let selectedCueStartMs = 0;
        let learningResumeOnClose = false;
        const sharedInspector = window.JularrLanguageInspector;
        const sharedInspectorAvailable = sharedInspector?.available === true;

        const openLearning = (cue, token = null, selectedElement = null) => {
            if (!overlay || !cue) return;
            overlay.querySelectorAll('[aria-pressed="true"]').forEach(element =>
                element.removeAttribute("aria-pressed"));
            if (selectedElement instanceof HTMLElement) {
                selectedElement.setAttribute("aria-pressed", "true");
            }

            selectedCueStartMs = cue.startMs;

                if (sharedInspectorAvailable) {
                    const sentence = design.cueText(cue);
                    void sharedInspector.open(token ? token.surface : sentence, {
                        sentence,
                        cueStartMs: cue.startMs
                    });
                    return;
                }

                if (!learningTools) {
                    return;
                }

                if (inspector.hidden) {
                    learningResumeOnClose = !video.paused && !video.ended;
                }

                video.pause();

                if (token) {
                    learningKicker.textContent = "Word";
                    word.textContent = token.canonical || token.surface;
                    reading.textContent = token.reading || "";
                    meaning.textContent = token.meaning || "No local meaning available yet.";
                    state.textContent = token.state || "New";
                    state.hidden = false;
                } else {
                    learningKicker.textContent = "Sentence";
                    word.textContent = design.cueText(cue);
                    reading.textContent = "";
                    meaning.textContent = "Tap a highlighted word in the subtitle to inspect its reading and meaning.";
                    state.textContent = "";
                    state.hidden = true;
                }

                inspector.hidden = false;
                closeLearning.focus();
            };

            const closeLearningSheet = (resume = true) => {
                if (!learningTools || inspector.hidden) {
                    return;
                }

                inspector.hidden = true;
                overlay.querySelectorAll('[aria-pressed="true"]').forEach(element =>
                    element.removeAttribute("aria-pressed"));

                const shouldResume = resume && learningResumeOnClose;
                learningResumeOnClose = false;
                if (shouldResume) {
                    void video.play().catch(() => {});
                }
            };

        if (sharedInspectorAvailable) {
            let inspectorResumeOnClose = false;

            sharedInspector.addEventListener("open", () => {
                inspectorResumeOnClose = !video.paused && !video.ended;
                video.pause();
            });

            sharedInspector.addEventListener("close", () => {
                const shouldResume = inspectorResumeOnClose;
                inspectorResumeOnClose = false;
                if (shouldResume) {
                    void video.play().catch(() => {});
                }
            });

            sharedInspector.addEventListener("statechange", event => {
                const detail = event.detail || {};
                if (!detail.text) {
                    return;
                }

                let changed = false;
                for (const cue of getCues()) {
                    for (const cueToken of cue.tokens || []) {
                        if ((cueToken.canonical || cueToken.surface) === detail.text) {
                            cueToken.state = detail.state;
                            changed = true;
                        }
                    }
                }

                if (changed && getActiveIndex() >= 0) {
                    renderActiveCue();
                }
            });
        }

        root.addEventListener(design.actionEvent, event => {
            const detail = event.detail || {};
            switch (detail.action) {
                case design.actions.openWord:
                    openLearning(detail.cue, detail.token, detail.element);
                    break;
                case design.actions.learnCurrentCue:
                    openLearning(detail.cue);
                    break;
                case design.actions.closeOverlay:
                    closeLearningSheet(true);
                    break;
            }
        });

        replay?.addEventListener("click", () =>
            design.dispatch(root, design.actions.repeatCurrentCue));
        closeLearning?.addEventListener("click", () =>
            design.dispatch(root, design.actions.closeOverlay));

        root.addEventListener("keydown", event => {
            if (event.key === "Escape" && learningTools && !inspector.hidden) {
                event.preventDefault();
                design.dispatch(root, design.actions.closeOverlay);
            }
        });

        return Object.freeze({
            isSheetOpen: () => learningTools && !inspector.hidden,
            selectedCueStartMs: () => selectedCueStartMs,
            close: closeLearningSheet
        });
    };

    window.JularrPlayerLearning = Object.freeze({ renderCue, isClickableToken, attachInspector });
})();
