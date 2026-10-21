import "./CodeSample.scss";
import { onPage } from "../../Modules/Navigation/PageSwap";

// Copies the text with the clipboard, or, where the page may not use it (an address that is not secure), with the selection.
async function copyText(text: string, source: HTMLElement): Promise<boolean> {
    if (navigator.clipboard) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fall through to the selection.
        }
    }

    const range = document.createRange();
    range.selectNodeContents(source);
    const selection = getSelection();
    selection?.removeAllRanges();
    selection?.addRange(range);
    const copied = document.execCommand("copy");
    selection?.removeAllRanges();
    return copied;
}

function initializeCodeSamples(root: ParentNode = document): void {
    for (const button of Array.from(root.querySelectorAll<HTMLButtonElement>("[data-code-copy]"))) {
        const source = button.closest(".code-sample")?.querySelector<HTMLElement>("[data-code-text]");
        if (!source || !button.hidden) continue;
        button.hidden = false;
        let reset: ReturnType<typeof setTimeout> | undefined;
        button.addEventListener("click", async () => {
            if (!(await copyText(source.textContent ?? "", source))) return;
            button.dataset.state = "copied";
            button.setAttribute("aria-label", button.dataset.copied ?? "");
            clearTimeout(reset);
            reset = setTimeout(() => {
                delete button.dataset.state;
                button.setAttribute("aria-label", button.dataset.copy ?? "");
            }, 1800);
        });
    }
}

onPage(import.meta.url, () => initializeCodeSamples());
