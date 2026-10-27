import { onPage } from "../../../Modules/Navigation/PageSwap";
import { insertToken, previewAddress } from "./Naming";

const patternForms = () => Array.from(document.querySelectorAll<HTMLFormElement>('form[data-form-kind$="UpdateNamingPatternDataV1"]'));
let lastField: HTMLInputElement | undefined;
const pending = new WeakMap<HTMLFormElement, AbortController>();
let timer: ReturnType<typeof setTimeout> | undefined;

// The example under a pattern follows what is typed, asked from the Backend, which judges a pattern as saving does.
async function showExample(form: HTMLFormElement): Promise<void> {
    const target = form.nextElementSibling?.querySelector<HTMLElement>("[data-naming-example]");
    const type = new URL(form.action, location.href).searchParams.get("mediaTypeId");
    const folder = form.querySelector<HTMLInputElement>('[name="Folder"]')?.value ?? "";
    const file = form.querySelector<HTMLInputElement>('[name="File"]')?.value ?? "";
    if (!target || !type || file.trim() === "") return;
    const options: Record<string, string> = {};
    const subtype = new URL(form.action, location.href).searchParams.get("mediaSubtypeId");
    if (subtype) options.mediaSubtypeId = subtype;
    for (const name of ["DailyFile", "SeasonFolder"]) options[name] = form.querySelector<HTMLInputElement>(`[name="${name}"]`)?.value ?? "";
    const settings = form.closest(".ui-section")?.querySelector<HTMLFormElement>('form[data-form-kind$="UpdateNamingSettingsDataV1"]');
    for (const name of ["RenamesFiles", "ReplacesIllegalCharacters", "ColonReplacementId", "CustomColonReplacement", "SpecialsFolder", "MultiEpisodeStyleId"]) {
        const field = settings?.querySelector<HTMLInputElement | HTMLSelectElement>(`[name="${name}"]`);
        if (field) options[name] = field instanceof HTMLInputElement && field.type === "checkbox" ? String(field.checked) : field.value;
    }
    pending.get(form)?.abort();
    const controller = new AbortController();
    pending.set(form, controller);
    try {
        const response = await fetch(previewAddress(type, folder, file, options), { credentials: "same-origin", signal: controller.signal, headers: { Accept: "application/json" } });
        if (!response.ok) return;
        const answer = await response.json() as { example?: string | null; problem?: string | null; multiExample?: string; dailyExample?: string; specialsExample?: string };
        target.classList.toggle("naming-example--problem", answer.problem != null);
        target.textContent = answer.example ?? target.getAttribute("data-problem-" + answer.problem) ?? "";
        for (const [attribute, value] of [["multi", answer.multiExample], ["daily", answer.dailyExample], ["specials", answer.specialsExample]]) {
            const output = form.nextElementSibling?.querySelector<HTMLElement>(`[data-naming-${attribute}]`);
            if (output) { output.textContent = value ?? ""; output.closest("p")!.hidden = !value; }
        }
    } catch {
        // A pattern in the middle of typing, or no answer: the last example stays.
    }
}

document.addEventListener("focusin", event => {
    const field = event.target;
    if (!(field instanceof HTMLInputElement)) return;
    if (field.closest('form[data-form-kind$="UpdateNamingPatternDataV1"]')
        || field.name === "SpecialsFolder" && field.closest('form[data-form-kind$="UpdateNamingSettingsDataV1"]')) lastField = field;
});

function changed(event: Event): void {
    const field = event.target;
    if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement)) return;
    const form = field.closest<HTMLFormElement>('form[data-form-kind$="UpdateNamingPatternDataV1"]');
    const forms = form ? [form] : Array.from(field.closest(".ui-section")?.querySelectorAll<HTMLFormElement>('form[data-form-kind$="UpdateNamingPatternDataV1"]') ?? []);
    clearTimeout(timer);
    timer = setTimeout(() => { for (const item of forms) void showExample(item); }, 250);
}
document.addEventListener("input", changed);
document.addEventListener("change", changed);

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

onPage(import.meta.url, () => { lastField = undefined; for (const form of patternForms()) void showExample(form); });
