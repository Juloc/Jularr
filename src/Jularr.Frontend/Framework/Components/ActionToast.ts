import { hintMilliseconds, hintText } from "../../Modules/Navigation/Action";

// Shows a hint for a moment; the next one replaces it.
export function showHint(text: string, failed = false): void {
    const toast = document.querySelector<HTMLElement>("[data-action-toast]");
    const label = toast?.querySelector<HTMLElement>(".action-toast__text");
    if (!toast || !label || text.length === 0) return;
    label.textContent = text;
    toast.classList.toggle("action-toast--failed", failed);
    toast.classList.remove("action-toast--show");
    void toast.offsetWidth;
    toast.style.setProperty("--action-toast-time", hintMilliseconds(text) + "ms");
    toast.classList.add("action-toast--show");
}

let installed = false;

export function installActionToast(): void {
    if (installed) return;
    installed = true;
    document.addEventListener("jularr:hint", event => {
        const detail = (event as CustomEvent<{ text?: string; failed?: boolean } | string>).detail;
        const text = hintText(detail);
        if (text) showHint(text, typeof detail === "object" && detail?.failed === true);
    });
}
