import { chooseAll } from "./ManualImport";

// One switch chooses or releases every file; each box announces its change, so the dock counts them as before.
document.addEventListener("change", event => {
    const switchAll = event.target;
    if (!(switchAll instanceof HTMLInputElement) || !switchAll.hasAttribute("data-manual-all")) return;
    chooseAll(Array.from(document.querySelectorAll<HTMLInputElement>("input[data-dock-select]")), switchAll.checked);
});

// Enter in the dialog's search field searches instead of applying.
document.addEventListener("keydown", event => {
    const field = event.target;
    if (event.key !== "Enter" || !(field instanceof HTMLInputElement) || field.name !== "MatchQuery") return;
    event.preventDefault();
    document.querySelector<HTMLButtonElement>('button[name="Intent"][value="searchMatch"]')?.click();
});
