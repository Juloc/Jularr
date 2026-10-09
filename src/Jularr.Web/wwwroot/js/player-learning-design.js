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

    window.JularrPlayerLearning = Object.freeze({ renderCue, isClickableToken });
})();
