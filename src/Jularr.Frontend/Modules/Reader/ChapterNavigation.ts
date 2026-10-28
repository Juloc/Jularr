// Another chapter of the same book: opened in place by the reader when it can, else as a page.
let open: ((address: string) => void) | undefined;

export function handleChapters(handler: (address: string) => void): void {
    open = handler;
}

export function openChapter(address: string): void {
    if (open) open(address);
    else location.assign(address);
}
