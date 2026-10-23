export interface SpeechModel {
    id: string; version: string; name: string; language: string; voice: string;
    license: string; licenseUrl: string; sizeBytes: number; sha256: string;
}
export interface InstalledModel extends SpeechModel { modelHash: string; tokensHash: string; modelBytes: number; tokensBytes: number }
export interface ModelFiles { model: ArrayBuffer; tokens: ArrayBuffer }
const Database = 'jularr-speech-models';

function open(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(Database, 1);
        let settled = false;
        const failed = (error: unknown) => { if (!settled) { settled = true; clearTimeout(timer); reject(error); } };
        const timer = window.setTimeout(() => failed(new Error('Speech storage timed out.')), 10_000);
        request.onupgradeneeded = () => { request.result.createObjectStore('metadata', { keyPath: 'id' }); request.result.createObjectStore('files'); };
        request.onsuccess = () => { clearTimeout(timer); if (settled) request.result.close(); else { settled = true; resolve(request.result); } };
        request.onerror = () => failed(request.error);
        request.onblocked = () => failed(new Error('Speech storage is blocked.'));
    });
}

export async function installedModels(): Promise<InstalledModel[]> {
    const database = await open();
    try {
        return await new Promise((resolve, reject) => {
            const request = database.transaction('metadata').objectStore('metadata').getAll();
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    } finally { database.close(); }
}

export async function modelFiles(model: InstalledModel): Promise<ModelFiles> {
    const database = await open();
    let files: ModelFiles;
    try {
        files = await new Promise((resolve, reject) => {
            const request = database.transaction('files').objectStore('files').get(model.id);
            request.onsuccess = () => request.result ? resolve(request.result) : reject(new Error('Speech model is not installed.'));
            request.onerror = () => reject(request.error);
        });
    } finally { database.close(); }
    if (!(files.model instanceof ArrayBuffer) || !(files.tokens instanceof ArrayBuffer)
        || files.model.byteLength !== model.modelBytes || files.tokens.byteLength !== model.tokensBytes
        || !model.modelBytes || model.modelBytes > 192 * 1024 * 1024 || !model.tokensBytes || model.tokensBytes > 1024 * 1024
        || await sha256(files.model) !== model.modelHash || await sha256(files.tokens) !== model.tokensHash) throw new Error('Installed speech model failed verification.');
    return files;
}

export async function saveModel(model: SpeechModel, files: ModelFiles, signal?: AbortSignal): Promise<void> {
    const metadata: InstalledModel = { ...model, modelHash: await sha256(files.model), tokensHash: await sha256(files.tokens),
        modelBytes: files.model.byteLength, tokensBytes: files.tokens.byteLength };
    const database = await open();
    try {
        await new Promise<void>((resolve, reject) => {
            signal?.throwIfAborted();
            const transaction = database.transaction(['metadata', 'files'], 'readwrite');
            const abort = () => {
                try { transaction.abort(); }
                catch (error) { if (!(error instanceof DOMException && error.name === 'InvalidStateError')) throw error; }
            };
            signal?.addEventListener('abort', abort, { once: true });
            transaction.objectStore('metadata').put(metadata);
            transaction.objectStore('files').put(files, model.id);
            transaction.oncomplete = () => { signal?.removeEventListener('abort', abort); resolve(); };
            transaction.onabort = transaction.onerror = () => { signal?.removeEventListener('abort', abort); reject(transaction.error ?? new Error('Speech model could not be stored.')); };
        });
    } finally { database.close(); }
}

export async function removeModel(id: string): Promise<void> {
    const database = await open();
    try {
        await new Promise<void>((resolve, reject) => {
            const transaction = database.transaction(['metadata', 'files'], 'readwrite');
            transaction.objectStore('metadata').delete(id);
            transaction.objectStore('files').delete(id);
            transaction.oncomplete = () => resolve();
            transaction.onabort = transaction.onerror = () => reject(transaction.error ?? new Error('Speech model could not be removed.'));
        });
    } finally { database.close(); }
}

export async function sha256(bytes: ArrayBuffer): Promise<string> {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    return Array.from(digest, value => value.toString(16).padStart(2, '0')).join('');
}
