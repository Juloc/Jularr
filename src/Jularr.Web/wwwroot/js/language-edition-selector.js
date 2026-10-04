// Shared Language & Edition selector (docs/mockups/language-edition-selector/SPEC.md; contract in _LanguageEditionSelector.cshtml).
// Opens the native <dialog>, filters edition rows by the chosen language, and announces a selection as a cancelable
// "jularr:edition-selected" event before the row's link navigates. Hosts that switch in place call preventDefault().
(() => {
    const dialogs = new Map();

    // The language chips are shortcuts of the full dropdown; both always show the same language.
    const showLanguage = (dialog, language) => {
        dialog.querySelector("[data-language-edition-language]").value = language;
        for (const row of dialog.querySelectorAll("[data-language-edition-row]")) {
            row.hidden = row.dataset.language !== language;
        }

        for (const chip of dialog.querySelectorAll("[data-language-edition-chip]")) {
            chip.setAttribute("aria-pressed", String(chip.dataset.languageEditionChip === language));
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
        let opener = null;

        for (const trigger of document.querySelectorAll(`[data-language-edition-open="${dialog.id}"]`)) {
            trigger.addEventListener("click", () => {
                opener = trigger;
                if (!dialog.open) {
                    dialog.showModal();
                }
            });
        }

        dialog.addEventListener("close", () => opener?.focus());
        dialog.addEventListener("click", event => {
            if (event.target === dialog) {
                dialog.close();
            }
        });
        const select = dialog.querySelector("[data-language-edition-language]");
        select.addEventListener("change", () => {
            dialog.querySelector("[data-language-edition-error]").hidden = true;
            showLanguage(dialog, select.value);
        });
        for (const chip of dialog.querySelectorAll("[data-language-edition-chip]")) {
            chip.addEventListener("click", () => {
                dialog.querySelector("[data-language-edition-error]").hidden = true;
                showLanguage(dialog, chip.dataset.languageEditionChip);
            });
        }

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
