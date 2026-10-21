// What an action (a form that posts) updates, and what stays: the pure rules behind ActionSwap.ts.

export interface RegionNode {
    readonly id: string;
    readonly dataset: Record<string, string | undefined>;
    readonly parentElement: RegionNode | null;
}

// The nearest region around an element: a container with an id that the page marked with data-region.
export function regionIdOf(start: RegionNode | null): string | null {
    for (let node = start; node; node = node.parentElement) {
        if (node.id && node.dataset.region !== undefined) return node.id;
    }

    return null;
}

export const selectorOf = (id: string): string => "#" + id.replace(/[^\w-]/g, character => "\\" + character);

// An answer shows the same page again when it comes from the same path; another path is a navigation (a redirect to the list, a new item).
export function isSamePage(answer: string, current: { pathname: string; origin: string }): boolean {
    try {
        const url = new URL(answer, current.origin);
        return url.origin === current.origin && url.pathname.toLowerCase() === current.pathname.toLowerCase();
    } catch {
        return false;
    }
}

// The text of a hint from an HX-Trigger payload (a bare string or an object with text).
export function hintText(detail: unknown): string | null {
    const value = typeof detail === "string" ? detail : detail && typeof detail === "object" ? (detail as { text?: unknown; value?: unknown }).text ?? (detail as { value?: unknown }).value : null;
    return typeof value === "string" && value.trim().length > 0 ? value.trim() : null;
}

// About two seconds; longer texts stay a little longer so they can be read, never more than seven.
export function hintMilliseconds(text: string): number {
    return Math.min(7000, 2200 + Math.max(0, text.length - 30) * 45);
}

export interface FormFingerprint {
    readonly action: string;
    readonly values: ReadonlyMap<string, string>;
}

// The form among the new ones that is the old one: the same address, and of those the one that agrees in most hidden values
// (the row it belongs to); what the action itself flips (a switch's value) may differ.
export function matchForm(before: FormFingerprint, candidates: readonly FormFingerprint[]): number {
    let best = -1;
    let bestScore = -1;
    candidates.forEach((candidate, index) => {
        if (candidate.action !== before.action) return;
        let score = 0;
        before.values.forEach((value, name) => { if (candidate.values.get(name) === value) score++; });
        if (score > bestScore) {
            best = index;
            bestScore = score;
        }
    });
    return best;
}
