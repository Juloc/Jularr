// "Chapter not downloaded yet" state: while a queued download operation is
// pending, poll the chapter status and return to the same reader location once
// the text is cached. Without JavaScript the page still works via reloads.
(() => {
    const root = document.querySelector("[data-novel-chapter-preparation]");
    if (!root || root.dataset.pending !== "true") return;

    const statusUrl = root.dataset.statusUrl;
    const readyUrl = root.dataset.readyUrl;
    const message = root.querySelector("[data-preparation-message]");
    const action = root.querySelector("[data-preparation-action]");
    if (!statusUrl || !readyUrl) return;

    const maxAttempts = 90;

    const poll = async attempt => {
        try {
            const response = await fetch(statusUrl, {
                credentials: "same-origin",
                cache: "no-store",
                headers: { "X-Requested-With": "fetch" }
            });

            if (response.ok) {
                const status = await response.json();
                if (status.ready) {
                    window.location.replace(readyUrl);
                    return;
                }

                if (status.status !== "queued" && status.status !== "running") {
                    if (message) message.textContent = status.message || root.dataset.failedText;
                    if (action) {
                        action.disabled = false;
                        action.textContent = root.dataset.retryText;
                    }
                    return;
                }
            } else if ([401, 403, 404].includes(response.status)) {
                // Access revoked or the chapter is gone: waiting longer cannot help.
                if (message) message.textContent = root.dataset.failedText;
                if (action) {
                    action.disabled = false;
                    action.textContent = root.dataset.retryText;
                }
                return;
            }
        } catch {
            // Transient network errors: keep polling.
        }

        if (attempt + 1 >= maxAttempts) {
            if (message) message.textContent = root.dataset.stalledText;
            if (action) action.disabled = false;
            return;
        }

        setTimeout(() => void poll(attempt + 1), 2000);
    };

    setTimeout(() => void poll(0), 1500);
})();
