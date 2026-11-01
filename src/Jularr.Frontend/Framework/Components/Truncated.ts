// Marks the cut-off one-line texts of Truncated so that only they show their full text, and makes them reachable by keyboard and touch.
export function markTruncated(root: ParentNode): void {
    for (const element of Array.from(root.querySelectorAll<HTMLElement>(".truncated"))) {
        const text = element.querySelector<HTMLElement>(".truncated__text");
        const clipped = text !== null && text.scrollWidth > text.clientWidth;
        element.classList.toggle("truncated--clipped", clipped);

        // A link inside is already focusable; plain text becomes focusable while it is cut off.
        if (!clipped) element.removeAttribute("tabindex");
        else if (element.querySelector("a, button") === null) element.setAttribute("tabindex", "0");
    }
}

export interface Box { left: number; top: number; right: number; bottom: number }

// Where the full text of a cut-off text goes: under it, level with its left edge, kept inside the window; above it when there is no room below.
export function tipPosition(text: Box, tip: { width: number; height: number }, window: { width: number; height: number }): { left: number; top: number } {
    const gap = 4;
    const edge = 8;
    const left = Math.max(edge, Math.min(text.left, window.width - tip.width - edge));
    const below = text.bottom + gap;
    const top = below + tip.height > window.height - edge ? Math.max(edge, text.top - gap - tip.height) : below;
    return { left, top };
}

// The tip shows with the hover or the focus; it is placed while it shows.
export function placeTip(element: HTMLElement): void {
    const tip = element.querySelector<HTMLElement>(".truncated__tip");
    if (!tip || !element.classList.contains("truncated--clipped")) return;
    const text = element.getBoundingClientRect();
    const spot = tipPosition(text, { width: tip.offsetWidth, height: tip.offsetHeight }, { width: innerWidth, height: innerHeight });
    tip.style.left = Math.round(spot.left) + "px";
    tip.style.top = Math.round(spot.top) + "px";
}
