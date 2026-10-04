// Shared Language & Edition selector (docs/mockups/language-edition-selector/SPEC.md; contract in _LanguageEditionSelector.cshtml).
// Opens the native <dialog>, filters edition rows by the chosen language, and announces a selection as a cancelable
// "jularr:edition-selected" event before the row's link navigates. Hosts that switch in place call preventDefault().
(() => {
    const dialogs = new Map();

    const showLanguage = dialog => {
        const language = dialog.querySelector("[data-language-edition-language]").value;
        for (const row of dialog.querySelectorAll("[data-language-edition-row]")) {
            row.hidden = row.dataset.language !== language;
        }
    };

    const showError = (id, text) => {
        const error = dialogs.get(id)?.querySelector("[data-language-edition-error]");
        if (error) {
            error.textContent = text;
            error.hidden = false;
        }
    };

    for (const dialog of document.querySelectorAll("[data-language-edition-dialog]")) {
        dialogs.set(dialog.id, dialog);
        const trigger = document.querySelector(`[data-language-edition-open="${dialog.id}"]`);

        trigger?.addEventListener("click", () => {
            if (!dialog.open) {
                dialog.showModal();
            }
        });
        dialog.addEventListener("close", () => trigger?.focus());
        dialog.addEventListener("click", event => {
            if (event.target === dialog) {
                dialog.close();
            }
        });
        dialog.querySelector("[data-language-edition-language]").addEventListener("change", () => {
            dialog.querySelector("[data-language-edition-error]").hidden = true;
            showLanguage(dialog);
        });

        for (const link of dialog.querySelectorAll("a[data-edition-key]")) {
            link.addEventListener("click", event => {
                const row = link.closest("[data-language-edition-row]");
                const proceed = dialog.dispatchEvent(new CustomEvent("jularr:edition-selected", {
                    cancelable: true,
                    detail: { selectorId: dialog.id, key: link.dataset.editionKey, language: row.dataset.language, url: link.href }
                }));
                if (!proceed) {
                    event.preventDefault();
                    dialog.close();
                    return;
                }

                link.setAttribute("aria-busy", "true");
            });
        }
    }

    window.JularrLanguageEditionSelector = { showError };

    // Returning with the back button restores the page from cache with a row still marked busy.
    window.addEventListener("pageshow", () => {
        for (const busy of document.querySelectorAll("[data-language-edition-dialog] [aria-busy]")) {
            busy.removeAttribute("aria-busy");
        }
    });
})();
