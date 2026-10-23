// The translation worker, bundled beside the reader.
export function createTranslationWorker(): Worker {
    return new Worker(new URL("./translation.worker.ts", import.meta.url));
}
