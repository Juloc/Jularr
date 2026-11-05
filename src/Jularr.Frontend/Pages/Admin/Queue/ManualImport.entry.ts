import { inserted, onPage } from "../../../Modules/Navigation/PageSwap";
import { chooseAll, fill, tally } from "./ManualImport";

// The dialog's rows came again (a change of a file swapped them): its dock counts the chosen ones again.
onPage(import.meta.url, () => {
    document.querySelector<HTMLInputElement>("#manual-import input[data-dock-select]")?.dispatchEvent(new Event("change", { bubbles: true }));
}, { partial: true });

// One switch chooses or releases every file that can go; each box announces its change, so the dock counts them as before.
document.addEventListener("change", event => {
    const switchAll = event.target;
    if (!(switchAll instanceof HTMLInputElement) || !switchAll.hasAttribute("data-manual-all")) return;
    chooseAll(Array.from(document.querySelectorAll<HTMLInputElement>("#manual-import input[data-dock-select]")), switchAll.checked);
});

// Enter in the dialog's search field searches instead of applying.
document.addEventListener("keydown", event => {
    const field = event.target;
    if (event.key !== "Enter" || !(field instanceof HTMLInputElement) || field.name !== "MatchQuery") return;
    event.preventDefault();
    document.querySelector<HTMLButtonElement>('button[name="Intent"][value="searchMatch"]')?.click();
});

// Import imports the chosen files one after another: each row first waits, then shows that it is moved, then where it went or why it failed; the figures count along and the
// button says how far it is. Without script the button posts the whole choice and the page shows the result.
document.addEventListener("click", event => {
    const button = (event.target as Element | null)?.closest?.<HTMLButtonElement>("button[data-manual-start]");
    const form = document.getElementById("manual-import");
    if (!button || !(form instanceof HTMLFormElement) || form.dataset.manualRunning) return;
    const boxes = Array.from(form.querySelectorAll<HTMLInputElement>("input[data-dock-select]")).filter(box => box.checked && !box.disabled);
    if (boxes.length === 0) return;
    event.preventDefault();
    void run(form, button, boxes);
}, true);

async function run(form: HTMLFormElement, button: HTMLButtonElement, boxes: HTMLInputElement[]): Promise<void> {
    form.dataset.manualRunning = "true";
    const choosable = form.querySelectorAll("input[data-dock-select]:not(:disabled)").length;
    const label = button.querySelector(".action-dock__text");
    const before = label?.textContent ?? "";
    const summary = form.querySelector<HTMLElement>("[data-manual-summary]");
    const outcomes: string[] = [];
    const rows = boxes.map(box => {
        const row = box.closest<HTMLElement>(".list-row");
        const status = row?.querySelector<HTMLElement>("[data-manual-status]") ?? null;
        const path = row?.querySelector<HTMLElement>("[data-manual-path]")?.dataset.manualPath ?? "";
        return { box, row, status, path };
    });

    // Every chosen file waits first, so the order is clear before anything moves.
    for (const { status } of rows) status?.replaceChildren(chip(form, "waiting"));
    button.disabled = true;
    if (summary) summary.hidden = false;
    count(form, outcomes, choosable);

    for (const [index, { box, row, status, path }] of rows.entries()) {
        if (label) label.textContent = fill(form.dataset.textProgress, index + 1, rows.length);
        status?.replaceChildren(chip(form, "moving"));
        const data = new FormData(form);
        data.delete("Intent");
        data.set("RunPath", path);
        let outcome = "failed";
        try {
            const response = await fetch(form.dataset.manualRun ?? "", { method: "POST", body: data, credentials: "same-origin", headers: { Accept: "text/html" } });
            const holder = document.createElement("div");
            holder.innerHTML = await response.text();
            const answer = holder.querySelector<HTMLElement>("[data-manual-outcome]");
            if (!response.ok || !answer) throw new Error("no answer");
            outcome = answer.dataset.manualOutcome ?? "failed";

            // The chip stands in the status; where the file went, or why not, is the line under its name.
            const line = answer.querySelector<HTMLElement>(".manual-import__outcome");
            const under = row?.querySelector<HTMLElement>(".list-identity__sub");
            if (line && under) {
                under.replaceChildren(...Array.from(line.childNodes));
                under.classList.toggle("manual-import__failure", outcome !== "imported");
                line.remove();
                inserted(under);
            }

            status?.replaceChildren(answer);
            inserted(answer);
        } catch {
            const failed = chip(form, "failed");
            status?.replaceChildren(failed);
        }

        // A moved file is done: it can no longer be chosen; a failed one stays chosen for another try.
        row?.classList.remove("list-row--warning", "list-row--danger");
        row?.classList.add(outcome === "imported" ? "list-row--success" : "list-row--danger");
        if (outcome === "imported") {
            box.checked = false;
            box.disabled = true;
            box.dispatchEvent(new Event("change", { bubbles: true }));
            row?.querySelector("[data-manual-note]")?.remove();
        }

        outcomes.push(outcome);
        count(form, outcomes, choosable);
    }

    if (label) label.textContent = before;
    button.disabled = false;
    delete form.dataset.manualRunning;
}

// The chip of a state the page rendered for the script (waiting, moving, failed).
function chip(form: HTMLFormElement, state: string): Node {
    const template = form.querySelector<HTMLElement>(`[data-manual-chip="${state}"]`);
    return template?.firstElementChild?.cloneNode(true) ?? document.createTextNode(form.dataset[`text${state[0].toUpperCase()}${state.slice(1)}`] ?? "");
}

function count(form: HTMLFormElement, outcomes: string[], choosable: number): void {
    const figures = tally(outcomes, choosable);
    for (const [name, value] of Object.entries(figures)) {
        const cell = form.querySelector<HTMLElement>(`[data-manual-count="${name}"]`);
        if (cell) cell.textContent = value.toLocaleString(document.documentElement.lang || undefined);
    }
}
