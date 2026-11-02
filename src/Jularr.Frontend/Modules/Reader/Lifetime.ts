// A chapter opened in place replaces the whole reader. What one reader registered outside its own element (window and document
// listeners, timers, workers, speech) ends with it: each part takes the signal when it starts and leaves as on pagehide.
let current = new AbortController();

export const readerLifetime = (): AbortSignal => current.signal;

export function endReaderLifetime(): void {
    const ended = current;
    current = new AbortController();
    ended.abort();
}
