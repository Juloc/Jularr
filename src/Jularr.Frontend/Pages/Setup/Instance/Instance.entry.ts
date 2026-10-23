import { onPage } from "../../../Modules/Navigation/PageSwap";
import { dependantNames, switchesOf, withRequired } from "../../Admin/Modules/Modules";

// The module step: the starting sets, and the rules of the switches as the Backend enforces them when it saves. A module that needs another turns it on
// with itself; a module that others need stays on while they are, and its row says so in one line. The step saves once through its normal POST.

const boxOf = (row: Element | null): HTMLInputElement | null => row?.querySelector<HTMLInputElement>('input[type="checkbox"]') ?? null;

function setup(panel: HTMLElement): void {
    const presets = JSON.parse(panel.dataset.setupPresets ?? "{}") as Record<string, string[]>;
    const names = JSON.parse(panel.dataset.setupNames ?? "{}") as Record<string, string>;
    const choices = panel.querySelector<HTMLElement>(".option-presets");
    if (choices) choices.hidden = false;
    const modules = () => Array.from(panel.querySelectorAll<HTMLElement>("[data-module]"));
    const rowOf = (key: string) => panel.querySelector<HTMLElement>(`[data-module="${key}"]`);
    const timers = new WeakMap<Element, number>();

    function reason(row: Element, text: string): void {
        const line = row.querySelector<HTMLElement>(".option-row__reason");
        if (!line) return;
        clearTimeout(timers.get(line));
        line.textContent = text;
        line.hidden = text === "";
        if (text) timers.set(line, window.setTimeout(() => { line.hidden = true; }, 5000));
    }

    // The count of each group and the starting set the switches match, if any.
    function refresh(): void {
        for (const group of Array.from(panel.querySelectorAll<HTMLElement>(".option-group"))) {
            const boxes = Array.from(group.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
            const count = group.querySelector<HTMLElement>("[data-option-count]");
            if (count) count.textContent = (panel.dataset.setupCount ?? "{0}/{1}").replace("{0}", String(boxes.filter(box => box.checked).length)).replace("{1}", String(boxes.length));
        }

        const on = new Set(switchesOf(panel).filter(item => item.on).map(item => item.key));
        for (const button of Array.from(panel.querySelectorAll<HTMLElement>("[data-setup-preset]"))) {
            const set = presets[button.dataset.setupPreset ?? ""] ?? [];
            button.setAttribute("aria-pressed", String(set.length === on.size && set.every(key => on.has(key))));
        }
    }

    // What a module needs goes on with it.
    function cascade(key: string): void {
        for (const needed of withRequired(switchesOf(panel), key)) {
            const box = boxOf(rowOf(needed));
            if (box) box.checked = true;
        }
    }

    panel.addEventListener("change", event => {
        const box = event.target instanceof HTMLInputElement && event.target.type === "checkbox" ? event.target : null;
        const row = box?.closest<HTMLElement>("[data-module], [data-media]");
        if (!box || !row) return;
        modules().forEach(item => reason(item, ""));

        if (row.dataset.module) {
            if (box.checked) cascade(row.dataset.module);
            else {
                const dependants = switchesOf(panel).filter(item => item.on && item.requires.includes(row.dataset.module ?? "")).map(item => item.key);
                if (dependants.length > 0) {
                    box.checked = true;
                    // Two names and how many more: one line, whatever the number.
                    const more = dependants.length > 2 ? ` +${dependants.length - 2}` : "";
                    reason(row, `${panel.dataset.setupNeeded ?? ""}: ${dependantNames(names, dependants.slice(0, 2))}${more}`);
                }
            }
        }

        // A subtype is on only below one of its types.
        const parents = (row.dataset.mediaParents ?? "").split(" ").filter(Boolean);
        if (box.checked && parents.length > 0) {
            const boxes = parents.map(parent => boxOf(panel.querySelector(`[data-media="${parent}"]`)));
            if (!boxes.some(parent => parent?.checked)) { const first = boxes.find(Boolean); if (first) first.checked = true; }
        }

        refresh();
    });

    for (const button of Array.from(panel.querySelectorAll<HTMLButtonElement>("[data-setup-preset]"))) {
        button.addEventListener("click", () => {
            const selected = new Set(presets[button.dataset.setupPreset ?? ""] ?? []);
            for (const row of modules()) {
                const box = boxOf(row);
                if (box) box.checked = selected.has(row.dataset.module ?? "");
            }
            for (const key of selected) cascade(key);
            modules().forEach(item => reason(item, ""));
            refresh();
        });
    }

    refresh();
}

onPage(import.meta.url, () => {
    for (const panel of Array.from(document.querySelectorAll<HTMLElement>("[data-setup-presets]"))) setup(panel);
});
