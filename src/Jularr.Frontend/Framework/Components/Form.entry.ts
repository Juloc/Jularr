import { inserted, onPage } from "../../Modules/Navigation/PageSwap";
import { initializeRanges } from "./Range";
import { initializeSteppers } from "./NumberStepper";

// Refresh dependent fields from the server without saving the draft. The button remains usable without JavaScript.
function enhance(form: HTMLFormElement): void {
    initializeRanges(form);
    initializeSteppers(form);
    enhanceSizes(form);
    // A choice that saves itself needs no button; the button is for browsers without script.
    if (form.querySelector('[data-form-submit]')) form.querySelectorAll<HTMLButtonElement>('[data-form-submit-fallback]').forEach(button => { button.hidden = true; });
    if (form.dataset.refreshReady || !form.querySelector('[data-form-refresh]')) return;
    form.dataset.refreshReady = "true";
    form.querySelectorAll<HTMLButtonElement>('[data-form-refresh-submit]').forEach(button => { button.hidden = true; });
    let pending: AbortController | undefined;
    form.addEventListener("change", async event => {
        const field = event.target;
        if (!(field instanceof HTMLSelectElement || field instanceof HTMLInputElement) || !field.dataset.formRefresh) return;
        const action = new URL(field.dataset.formRefresh, location.href);
        if (action.origin !== location.origin) return;
        pending?.abort();
        const controller = pending = new AbortController();
        const values = new FormData(form);
        form.setAttribute("aria-busy", "true");
        try {
            const response = await fetch(action, { method: "POST", body: values, credentials: "same-origin", signal: controller.signal,
                headers: { Accept: "text/html" } });
            if (!response.headers.get("Content-Type")?.includes("text/html")) throw new Error("Expected a form.");
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const replacement = page.querySelector<HTMLFormElement>(`form[data-form-kind="${CSS.escape(form.dataset.formKind ?? "")}"]`);
            if (!replacement) throw new Error("Expected a matching form.");
            if (controller.signal.aborted) return;
            form.replaceWith(replacement);
            inserted(replacement);
            replacement.dispatchEvent(new CustomEvent("jularr:draft", { bubbles: true }));
            enhance(replacement);
            replacement.querySelector<HTMLElement>(`[name="${CSS.escape(field.name)}"]`)?.focus({ preventScroll: true });
        } catch {
            if (!controller.signal.aborted) {
                const button = Array.from(form.querySelectorAll<HTMLButtonElement>('[data-form-refresh-submit]'))
                    .find(item => item.formAction === action.href);
                if (button) form.requestSubmit(button);
            }
        } finally {
            if (!controller.signal.aborted) form.removeAttribute("aria-busy");
        }
    });
}

// The change of such a choice is its save: the form posts as an action and the page keeps its place.
document.addEventListener("change", event => {
    const field = event.target;
    if ((field instanceof HTMLInputElement || field instanceof HTMLSelectElement) && field.dataset.formSubmit !== undefined) field.form?.requestSubmit();
});

// An optional choice that saves itself goes back to none when its chosen segment is clicked again (or Space or Enter is pressed on it).
document.addEventListener("pointerdown", event => {
    if (event.target instanceof HTMLInputElement && event.target.dataset.formClearable !== undefined) event.target.dataset.wasChecked = String(event.target.checked);
}, true);
document.addEventListener("click", event => {
    const field = event.target;
    if (!(field instanceof HTMLInputElement) || field.dataset.formClearable === undefined) return;
    const clear = field.dataset.wasChecked === "true";
    delete field.dataset.wasChecked;
    if (!clear) return;
    field.checked = false;
    field.form?.requestSubmit();
});
document.addEventListener("keydown", event => {
    const field = event.target;
    if (event.key !== " " && event.key !== "Enter" || !(field instanceof HTMLInputElement) || field.dataset.formClearable === undefined || !field.checked) return;
    event.preventDefault();
    field.checked = false;
    field.form?.requestSubmit();
});

onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLFormElement>('form').forEach(enhance);
    document.querySelectorAll<HTMLElement>('[data-form-local]').forEach(local => { initializeRanges(local); initializeSteppers(local); });
}, { partial: true });

// Unit changes affect presentation only; the typed field is always submitted in bytes.
function enhanceSizes(form: HTMLFormElement): void {
form.querySelectorAll<HTMLElement>('[data-byte-size]').forEach(control => {
    if (control.dataset.byteReady) return;
    const input = control.querySelector<HTMLInputElement>('[data-byte-input]');
    const unit = control.querySelector<HTMLSelectElement>('[data-byte-unit]');
    if (!input || !unit || !input.name) return;
    control.dataset.byteReady = 'true';
    const value = document.createElement('input');
    value.type = 'hidden';
    value.name = input.name;
    value.value = input.value;
    input.removeAttribute('name');
    input.removeAttribute('min');
    input.removeAttribute('max');
    input.step = 'any';
    control.append(value);
    let factor = 1n;
    if (!value.value || BigInt(value.value) === 0n) factor = 1073741824n;
    else {
        for (const candidate of [1099511627776n, 1073741824n, 1048576n]) {
            if (BigInt(value.value) % candidate === 0n) { factor = candidate; break; }
        }
    }
    function show(bytes: string): string {
        if (!bytes) return '';
        const size = BigInt(bytes);
        const whole = size / factor;
        const remainder = size % factor;
        if (remainder === 0n) return whole.toString();
        const fraction = (remainder * (10n ** 40n) / factor).toString().padStart(40, '0').replace(/0+$/, '');
        return whole.toString() + '.' + fraction;
    }
    function sync(): boolean {
        input!.setCustomValidity('');
        if (!input!.value) { value.value = ''; return true; }
        const match = /^(\d+)(?:\.(\d*))?$/.exec(input!.value);
        if (!match) { input!.setCustomValidity(control.dataset.byteInvalid ?? ''); return false; }
        const digits = match[2] ?? '';
        const numerator = BigInt(match[1] + digits) * factor;
        const denominator = 10n ** BigInt(digits.length);
        if (numerator % denominator !== 0n) { input!.setCustomValidity(control.dataset.byteInvalid ?? ''); return false; }
        value.value = (numerator / denominator).toString();
        return true;
    }
    unit.value = factor.toString();
    input.value = show(value.value);
    unit.disabled = false;
    input.addEventListener('input', sync);
    unit.addEventListener('change', () => {
        if (!sync()) { unit.value = factor.toString(); return; }
        factor = BigInt(unit.value);
        input.value = show(value.value);
    });
});
}

// A select whose options carry an icon shows the icon of the chosen option at the start of its field.
document.addEventListener("change", event => {
    const select = event.target;
    if (!(select instanceof HTMLSelectElement)) return;
    const icons = select.closest(".ui-field__control")?.querySelector<HTMLElement>("[data-option-icons]");
    if (!icons) return;
    for (const icon of Array.from(icons.querySelectorAll<HTMLElement>("[data-option-icon]"))) icon.hidden = icon.dataset.optionIcon !== select.value;
});
