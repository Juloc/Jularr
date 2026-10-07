// Setup → Instance. A starting point sets the module switches the same way the server does (the data-on-* attributes mirror
// InstanceModulePresets), so the owner sees the resulting module state before saving; touching a switch makes the starting
// point Custom. Without this script the server applies the chosen starting point itself.
(() => {
    const form = document.querySelector("[data-setup-instance]");
    if (!form) {
        return;
    }

    const starts = Array.from(form.querySelectorAll("[data-setup-start]"));
    const switches = Array.from(form.querySelectorAll("[data-setup-module]"));
    const markSelected = () => {
        for (const radio of starts) {
            radio.closest(".setup-start-card")?.classList.toggle("selected", radio.checked);
        }
    };

    for (const radio of starts) {
        radio.addEventListener("change", () => {
            const attribute = `data-on-${radio.value.toLowerCase()}`;
            for (const control of switches) {
                // A switch a starting point does not decide (the media types of Media Manager) has no value and keeps what the owner set.
                const value = radio.value === "Custom" ? "" : control.getAttribute(attribute);
                if (value) {
                    control.checked = value === "1";
                }
            }

            markSelected();
        });
    }

    for (const control of switches) {
        control.addEventListener("change", () => {
            const custom = starts.find((radio) => radio.value === "Custom");
            if (custom) {
                custom.checked = true;
                markSelected();
            }
        });
    }

    // The Home cards follow the media switches: a media type is offered while a module that serves it is on.
    const homeCards = Array.from(form.querySelectorAll("[data-home-modules]"));
    const syncHomeCards = () => {
        for (const card of homeCards) {
            card.hidden = !card.dataset.homeModules.split(" ").some((field) => form.querySelector(`[name="${field}"]`)?.checked);
        }

        form.querySelector("[data-home-layout]")?.dispatchEvent(new Event("home-layout:refresh"));
    };

    for (const control of switches) {
        control.addEventListener("change", syncHomeCards);
    }

    for (const radio of starts) {
        radio.addEventListener("change", syncHomeCards);
    }

    syncHomeCards();

    const accentInput = form.querySelector("[data-setup-accent]");
    const presets = Array.from(form.querySelectorAll("[data-setup-accent-preset]"));
    for (const preset of presets) {
        preset.addEventListener("click", () => {
            accentInput.value = preset.dataset.setupAccentPreset;
            for (const other of presets) {
                const selected = other === preset;
                other.classList.toggle("selected", selected);
                other.setAttribute("aria-checked", selected ? "true" : "false");
            }
        });
    }
})();
