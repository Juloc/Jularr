export function neuralWorker(): Worker {
    return new Worker(new URL('./neural.worker.ts', import.meta.url), { type: 'module' });
}
