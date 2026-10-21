// Which view a book with a PDF is read in, and when a better one is offered: the profile's choice wins, an open book never changes by itself.
export type ViewChoice = 'auto' | 'smart' | 'original';
export type ReadingView = 'smart' | 'original';

// The choice as the server stores it: none is automatic.
export const choiceOf = (stored: string | null | undefined): ViewChoice => stored === 'smart' || stored === 'original' ? stored : 'auto';

// Automatic follows the recommendation; a choice always wins. With no recommendation yet the original pages are there without any preparation.
export const effectiveView = (choice: ViewChoice, recommended: ReadingView | null): ReadingView => choice === 'auto' ? recommended ?? 'original' : choice;

// A better view is offered only to a profile that left the choice to Jularr, in the original pages, once the reading text passed its gate, and not again after it was dismissed.
export const offersSmartBook = (choice: ViewChoice, current: ReadingView, recommended: ReadingView | null, dismissed: boolean): boolean =>
    choice === 'auto' && current === 'original' && recommended === 'smart' && !dismissed;

// Waits between two looks at the book's state: soon at first, then ever less often, never more rarely than a minute; none once the state cannot change the offer.
export function nextLook(attempt: number): number {
    return Math.min(60_000, 5_000 * 2 ** Math.max(0, Math.min(attempt, 4)));
}

// The address of the other view at the same place: the page the profile is on.
export function viewAddress(address: string, view: ReadingView, page: number | null): string {
    const url = new URL(address, 'http://reader.invalid');
    url.searchParams.set('view', view);
    url.searchParams.delete('s');
    url.searchParams.delete('b');
    if (page !== null) url.searchParams.set('pg', String(page)); else url.searchParams.delete('pg');
    return url.pathname + url.search;
}
