// Continue Reading after leaving the reader; hiding it never touches the saved progress.
const HiddenKey = "jularr.continueReading.hidden";

// A dismissed bar returns once the reading position has changed.
export function isHidden(key: string | undefined, hidden: string | null): boolean {
    return !key || key === hidden;
}

export async function showContinueReading(): Promise<void> {
    const main = document.querySelector<HTMLElement>(".app-main");
    if (!main || main.querySelector(".continue-bar") || document.body.classList.contains("player-active")) return;

    let response: Response;
    try {
        response = await fetch("/Continue/Reading", { credentials: "same-origin", headers: { Accept: "text/html" } });
    } catch {
        return;
    }

    if (response.status !== 200 || !response.headers.get("Content-Type")?.includes("text/html")) return;
    const bar = new DOMParser().parseFromString(await response.text(), "text/html").querySelector<HTMLElement>(".continue-bar");
    if (!bar || isHidden(bar.dataset.continueKey, readHidden()) || !main.isConnected || document.body.classList.contains("player-active")) return;

    const shown = document.importNode(bar, true);
    shown.querySelector("[data-continue-dismiss]")?.addEventListener("click", () => {
        try {
            localStorage.setItem(HiddenKey, shown.dataset.continueKey ?? "");
        } catch {
            // Without storage the bar only hides on this page.
        }
        shown.remove();
    });
    main.append(shown);
}

function readHidden(): string | null {
    try {
        return localStorage.getItem(HiddenKey);
    } catch {
        return null;
    }
}
