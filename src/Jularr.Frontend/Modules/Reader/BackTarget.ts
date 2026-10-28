// The Back arrow of a book leaves it one step, to where the reader came from; only a book opened without a page of this app before it goes to the title's details.
export interface HistoryView {
    // History entries behind this one: the previous entry's address, null when there is none, undefined when the browser cannot tell.
    previous: string | null | undefined;
    length: number;
    referrer: string;
    origin: string;
}

function sameOrigin(address: string, origin: string): boolean {
    try {
        return new URL(address).origin === origin;
    } catch {
        return false;
    }
}

export function leavesByHistory(view: HistoryView): boolean {
    if (view.previous !== undefined) return view.previous !== null && sameOrigin(view.previous, view.origin);
    if (view.length <= 1) return false;
    return view.referrer === "" || sameOrigin(view.referrer, view.origin);
}
