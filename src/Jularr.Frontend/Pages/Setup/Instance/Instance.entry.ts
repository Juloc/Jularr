// Presets change only the shown choices. The step saves once through its normal POST.
for (const panel of Array.from(document.querySelectorAll<HTMLElement>("[data-setup-presets]"))) {
    const presets = JSON.parse(panel.dataset.setupPresets ?? "{}") as Record<string, number[]>;
    const choices = panel.querySelector<HTMLElement>(".setup-modules__presets");
    if (choices) choices.hidden = false;
    for (const button of Array.from(panel.querySelectorAll<HTMLButtonElement>("[data-setup-preset]"))) {
        button.addEventListener("click", () => {
            const selected = new Set(presets[button.dataset.setupPreset ?? ""] ?? []);
            for (const toggle of Array.from(panel.querySelectorAll<HTMLInputElement>('input[type="checkbox"][name$=".IsEnabled"]'))) {
                const prefix = toggle.name.slice(0, -"IsEnabled".length);
                const id = panel.querySelector<HTMLInputElement>(`input[name="${prefix}Id"]`);
                toggle.checked = selected.has(Number(id?.value));
            }
        });
    }
}
