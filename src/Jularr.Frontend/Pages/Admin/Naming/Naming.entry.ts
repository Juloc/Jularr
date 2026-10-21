import { onPage } from "../../../Modules/Navigation/PageSwap";
import { insertToken, previewAddress } from "./Naming";

const patternForms = () => Array.from(document.querySelectorAll<HTMLFormElement>('form[data-form-kind$="UpdateNamingPatternDataV1"]'));
let lastField: HTMLInputElement | undefined;
let pending: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;

// The example under a pattern follows what is typed, asked from the Backend, which judges a pattern as saving does.
async function showExample(form: HTMLFormElement): Promise<void> {
    const target = form.nextElementSibling?.querySelector<HTMLElement>("[data-naming-example]");
    const type = new URL(form.action, location.href).searchParams.get("mediaTypeId");
    const folder = form.querySelector<HTMLInputElement>('[name="Folder"]')?.value ?? "";
    const file = form.querySelector<HTMLInputElement>('[name="File"]')?.value ?? "";
    if (!target || !type || file.trim() === "") return;
    pending?.abort();
    const controller = pending = new AbortController();
    try {
        const response = await fetch(previewAddress(type, folder, file), { credentials: "same-origin", signal: controller.signal, headers: { Accept: "application/json" } });
        if (!response.ok) return;
        const answer = await response.json() as { example?: string | null; problem?: string | null };
        target.classList.toggle("naming-example--problem", answer.problem != null);
        target.textContent = answer.example ?? target.getAttribute("data-problem-" + answer.problem) ?? "";
    } catch {
        // A pattern in the middle of typing, or no answer: the last example stays.
    }
}

document.addEventListener("focusin", event => {
    if (event.target instanceof HTMLInputElement && event.target.closest('form[data-form-kind$="UpdateNamingPatternDataV1"]')) lastField = event.target;
});

document.addEventListener("input", event => {
    const form = event.target instanceof HTMLInputElement ? event.target.closest<HTMLFormElement>('form[data-form-kind$="UpdateNamingPatternDataV1"]') : null;
    if (!form) return;
    clearTimeout(timer);
    timer = setTimeout(() => void showExample(form), 250);
});

// A placeholder of the drawer goes into the field that was used last, the file name of the first pattern otherwise.
document.addEventListener("click", event => {
    const button = event.target instanceof Element ? event.target.closest<HTMLElement>("[data-naming-token]") : null;
    if (!button) return;
    const field = lastField?.isConnected ? lastField : patternForms()[0]?.querySelector<HTMLInputElement>('[name="File"]');
    if (!field) return;
    const { value, caret } = insertToken(field.value, field.selectionStart ?? field.value.length, field.selectionEnd ?? field.value.length, button.dataset.namingToken ?? "");
    field.value = value;
    field.dispatchEvent(new Event("input", { bubbles: true }));
    field.closest("dialog")?.close();
    field.focus();
    field.setSelectionRange(caret, caret);
});

onPage(import.meta.url, () => { lastField = undefined; });
