import { neuralWorker } from './NeuralWorker';
import { saveModel, type ModelFiles, type SpeechModel } from './ModelStore';

// Only called from the explicit device-install action; neither page setup nor Read aloud installs a model.
export async function installModel(model: SpeechModel, signal: AbortSignal): Promise<void> {
    if (!/^[a-f0-9]{64}$/.test(model.sha256) || !Number.isSafeInteger(model.sizeBytes) || model.sizeBytes <= 0 || model.sizeBytes > 128 * 1024 * 1024) throw new Error('Unsupported speech model size/hash.');
    signal = AbortSignal.any([signal, AbortSignal.timeout(180_000)]);
    const response = await fetch('/api/v1/speech/models/' + model.sha256 + '/file', { credentials: 'same-origin', signal });
    if (!response.ok || !response.body) throw new Error('Speech model is unavailable.');
    const reader = response.body.getReader();
    const bytes = new Uint8Array(model.sizeBytes);
    let offset = 0;
    try {
        while (true) {
            const next = await reader.read();
            if (next.done) break;
            if (offset + next.value.length > bytes.length) throw new Error('Speech download exceeds its declared size.');
            bytes.set(next.value, offset); offset += next.value.length;
        }
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
    if (offset !== bytes.length) throw new Error('Incomplete speech model download.');
    signal.throwIfAborted();
    const worker = neuralWorker();
    try {
        const files = await new Promise<ModelFiles>((resolve, reject) => {
            const cancel = () => { finish(); worker.terminate(); reject(signal.reason ?? new Error('Speech installation cancelled.')); };
            const finish = () => signal.removeEventListener('abort', cancel);
            signal.addEventListener('abort', cancel, { once: true });
            worker.onerror = event => { finish(); reject(new Error(event.message)); };
            worker.onmessage = event => {
                finish();
                if (event.data.type === 'unpacked') resolve(event.data.files);
                else reject(new Error(event.data.message ?? 'Speech installation failed.'));
            };
            worker.postMessage({ type: 'unpack', archive: bytes.buffer, model }, [bytes.buffer]);
        });
        signal.throwIfAborted();
        await saveModel(model, files, signal);
    } finally { worker.terminate(); }
}
