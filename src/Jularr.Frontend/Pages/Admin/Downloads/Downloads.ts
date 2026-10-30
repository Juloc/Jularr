// The rules of the Downloads page's queue apart from the page.

// A row moved one place up or down stands before the row at the returned index (the row count: at the end); null when it is already at that end.
export function beforeAfterMove(index: number, count: number, step: -1 | 1): number | null {
    if (index < 0 || step === -1 && index === 0 || step === 1 && index >= count - 1) return null;
    return step === -1 ? index - 1 : index + 2;
}
