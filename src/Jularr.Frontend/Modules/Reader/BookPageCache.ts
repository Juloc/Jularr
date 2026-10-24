// Pagination belongs to the browser layout. Store only completed numeric chapter counts,
// scoped to an immutable reading document and an exact layout fingerprint.
const Database = 'jularr-reader-pages';
const Store = 'layouts';
const MaximumLayouts = 64;

interface SavedPages { counts: number[]; savedAt: number }

function openPages(): Promise<IDBDatabase | undefined> {
    if (typeof indexedDB === 'undefined') return Promise.resolve(undefined);
    return new Promise(resolve => {
        try {
            const request = indexedDB.open(Database, 1);
            request.onupgradeneeded = () => request.result.createObjectStore(Store);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => resolve(undefined);
            request.onblocked = () => resolve(undefined);
        } catch { resolve(undefined); }
    });
}

export async function readBookPageCounts(key: string, sections: number): Promise<number[] | undefined> {
    const database = await openPages();
    if (!database) return;
    try {
        return await new Promise(resolve => {
            const request = database.transaction(Store, 'readonly').objectStore(Store).get(key);
            request.onsuccess = () => {
                const saved = request.result as SavedPages | undefined;
                const counts = saved?.counts;
                resolve(Array.isArray(counts) && counts.length === sections
                    && counts.every(count => Number.isSafeInteger(count) && count > 0) ? counts : undefined);
            };
            request.onerror = () => resolve(undefined);
        });
    } catch { return; }
    finally { database.close(); }
}

export async function writeBookPageCounts(key: string, counts: number[]): Promise<void> {
    if (!counts.length || !counts.every(count => Number.isSafeInteger(count) && count > 0)) return;
    const database = await openPages();
    if (!database) return;
    try {
        await new Promise<void>(resolve => {
            const transaction = database.transaction(Store, 'readwrite');
            const store = transaction.objectStore(Store);
            store.put({ counts: [...counts], savedAt: Date.now() } satisfies SavedPages, key);
            const cursor = store.openCursor();
            const entries: { key: IDBValidKey; savedAt: number }[] = [];
            cursor.onsuccess = () => {
                const entry = cursor.result;
                if (entry) {
                    entries.push({ key: entry.key, savedAt: Number(entry.value?.savedAt) || 0 });
                    entry.continue();
                } else {
                    entries.sort((a, b) => a.savedAt - b.savedAt);
                    entries.slice(0, Math.max(0, entries.length - MaximumLayouts)).forEach(old => store.delete(old.key));
                }
            };
            transaction.oncomplete = () => resolve();
            transaction.onabort = () => resolve();
            transaction.onerror = () => resolve();
        });
    } catch { /* A disabled or full cache must never block reading. */ }
    finally { database.close(); }
}
