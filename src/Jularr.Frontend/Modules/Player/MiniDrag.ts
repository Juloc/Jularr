// The bar of the minimized player sits in one of the four corners of the screen; a mouse drags it to another one, where it snaps to the nearest, and the corner is kept on this device.
// On a phone the bar is as wide as the screen and stays at the bottom.

export type Corner = "tl" | "tr" | "bl" | "br";

const StorageKey = "jularr.player.corner";
const Corners: readonly Corner[] = ["tl", "tr", "bl", "br"];
// A press that moves less than this is a tap.
const Threshold = 6;
// The bar is free to move only where it is a card of its own (the phone's bar spans the screen).
const Wide = "(min-width: 781px)";

export function savedCorner(): Corner {
    try {
        const stored = localStorage.getItem(StorageKey);
        if (Corners.includes(stored as Corner)) return stored as Corner;
    } catch { /* Storage is optional. */ }
    return "br";
}

export function nearestCorner(centerX: number, centerY: number, width: number, height: number): Corner {
    return ((centerY < height / 2 ? "t" : "b") + (centerX < width / 2 ? "l" : "r")) as Corner;
}

export function enableMiniDrag(host: HTMLElement, view: { mini: boolean }, signal: AbortSignal): void {
    host.addEventListener("pointerdown", event => {
        if (!view.mini || event.button !== 0 || event.pointerType === "touch" || !matchMedia(Wide).matches) return;
        if ((event.target as Element).closest("button, input, a, [popover]")) return;
        const origin = host.getBoundingClientRect();
        const startX = event.clientX;
        const startY = event.clientY;
        let dragging = false;

        const move = (moved: PointerEvent) => {
            const dx = moved.clientX - startX;
            const dy = moved.clientY - startY;
            if (!dragging) {
                if (Math.hypot(dx, dy) < Threshold) return;
                dragging = true;
                host.setPointerCapture(event.pointerId);
                host.classList.add("player-main--dragging");
            }
            const left = Math.min(Math.max(origin.left + dx, 0), window.innerWidth - origin.width);
            const top = Math.min(Math.max(origin.top + dy, 0), window.innerHeight - origin.height);
            host.style.inset = `${top}px auto auto ${left}px`;
        };

        const release = () => {
            host.removeEventListener("pointermove", move);
            host.removeEventListener("pointerup", release);
            host.removeEventListener("pointercancel", release);
            if (!dragging) return;
            const from = host.getBoundingClientRect();
            const corner = nearestCorner(from.left + from.width / 2, from.top + from.height / 2, window.innerWidth, window.innerHeight);
            try { localStorage.setItem(StorageKey, corner); } catch { /* Storage is optional. */ }
            host.style.removeProperty("inset");
            host.classList.remove("player-main--dragging");
            host.dataset.corner = corner;
            // It glides from where it was let go to its corner.
            const to = host.getBoundingClientRect();
            host.animate([{ translate: `${from.left - to.left}px ${from.top - to.top}px` }, { translate: "0 0" }], { duration: 180, easing: "ease-out" });
            // The click that ends the press is no tap on the bar.
            host.dataset.dragged = "";
            setTimeout(() => host.removeAttribute("data-dragged"), 60);
        };

        host.addEventListener("pointermove", move);
        host.addEventListener("pointerup", release);
        host.addEventListener("pointercancel", release);
    }, { signal });
}
