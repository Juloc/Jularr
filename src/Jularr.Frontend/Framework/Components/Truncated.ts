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
