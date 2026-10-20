import { chunkEnd, type SpeechItem, type SpeechOptions, type SpeechState } from './DeviceSession';
import { modelFiles, type InstalledModel } from './ModelStore';
import { neuralWorker } from './NeuralWorker';

// One lazy chunk, one worker and one audio source. A generation fence owns every async continuation.
export class NeuralSession {
    state: SpeechState = 'idle';
    private version = 0;
    private job = 0;
    private iterator?: Iterator<SpeechItem>;
    private item?: SpeechItem;
    private cursor = 0;
    private end = 0;
    private options?: SpeechOptions;
    private model?: InstalledModel;
    private context?: AudioContext;
    private source?: AudioBufferSourceNode;
    private gain?: GainNode;
    private worker?: Worker;
    private ready = false;
    private timer?: number;

    constructor(private changed: (state: SpeechState) => void, private chunk: (item: SpeechItem, offset: number, length: number) => void) {}

    start(items: Iterable<SpeechItem>, options: SpeechOptions, model: InstalledModel, context: AudioContext): void {
        this.stop();
        this.iterator = items[Symbol.iterator]();
        this.options = options;
        this.model = model;
        this.context = context;
        this.gain = context.createGain();
        this.gain.gain.value = options.volume;
        this.gain.connect(context.destination);
        const version = this.version;
        void context.resume().catch(() => { if (version === this.version) this.stop('failed'); });
        void this.advance();
    }

    stop(state: SpeechState = 'idle'): void {
        this.version++;
        this.releaseWorker();
        if (this.source) {
            this.source.onended = null;
            this.source.stop(); this.source.disconnect();
        }
        this.gain?.disconnect();
        if (this.context && this.context.state !== 'closed') void this.context.close().catch(() => {});
        this.source = this.gain = this.context = undefined;
        this.iterator = this.item = this.options = this.model = undefined;
        this.cursor = this.end = 0;
        this.setState(state);
    }

    pause(): void {
        if (this.state !== 'speaking' && this.state !== 'loading') return;
        this.setState('paused');
        if (!this.source) { this.version++; this.releaseWorker(); }
        const version = this.version;
        void this.context?.suspend().catch(() => { if (version === this.version) this.stop('failed'); });
    }

    resume(): void {
        if (this.state !== 'paused' || !this.context) return;
        const version = this.version;
        this.setState('loading');
        void this.context.resume().then(() => {
            if (version !== this.version || this.state !== 'loading') return;
            if (this.source) this.setState('speaking');
            else void this.advance();
        }).catch(() => { if (version === this.version) this.stop('failed'); });
    }

    private setState(state: SpeechState): void { this.state = state; this.changed(state); }

    private releaseWorker(): void {
        clearTimeout(this.timer);
        if (this.worker) {
            this.worker.onmessage = this.worker.onerror = this.worker.onmessageerror = null;
            this.worker.terminate();
        }
        this.worker = undefined;
        this.ready = false;
    }

    private deadline(): void {
        clearTimeout(this.timer);
        const version = this.version;
        this.timer = window.setTimeout(() => { if (version === this.version) this.stop('failed'); }, 90_000);
    }

    private async advance(): Promise<void> {
        if (!this.iterator || !this.model || !this.options || !this.context || this.state === 'paused') return;
        while (!this.item || this.cursor >= this.item.text.length) {
            const next = this.iterator.next();
            if (next.done) { this.stop(); return; }
            this.item = next.value; this.cursor = 0;
            if (!this.item.text.trim()) { this.item = undefined; continue; }
        }
        this.end = chunkEnd(this.item.text, this.cursor);
        if (this.ready) { this.generate(); return; }
        this.setState('loading');
        this.deadline();
        const version = this.version;
        try {
            const files = await modelFiles(this.model);
            if (version !== this.version || this.state !== 'loading') return;
            const worker = this.worker = neuralWorker();
            const current = () => version === this.version && this.worker === worker;
            worker.onerror = worker.onmessageerror = () => { if (current()) this.stop('failed'); };
            worker.onmessage = event => {
                if (!current()) return;
                const message = event.data;
                if (message.type === 'failed') { this.stop('failed'); return; }
                if (message.type === 'ready' && !this.ready && this.state === 'loading') { this.ready = true; this.generate(); }
                else if (message.type === 'audio' && message.id === this.job && this.state === 'loading') this.play(message.samples, message.sampleRate, version);
            };
            worker.postMessage({ type: 'load', files }, [files.model, files.tokens]);
        } catch { if (version === this.version) this.stop('failed'); }
    }

    private generate(): void {
        this.setState('loading'); this.deadline();
        this.worker!.postMessage({ type: 'generate', id: ++this.job, text: this.item!.text.slice(this.cursor, this.end),
            speed: this.options!.rate, voice: Number(this.model!.voice) });
    }

    private play(samples: Float32Array, sampleRate: number, version: number): void {
        clearTimeout(this.timer);
        if (!(samples instanceof Float32Array) || !samples.length || !Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 96000
            || samples.length > sampleRate * 120 || !this.context || !this.gain) { this.stop('failed'); return; }
        try {
            const buffer = this.context.createBuffer(1, samples.length, sampleRate);
            buffer.copyToChannel(samples as Float32Array<ArrayBuffer>, 0);
            const source = this.source = this.context.createBufferSource();
            source.buffer = buffer; source.connect(this.gain);
            source.onended = () => {
                if (version !== this.version || this.source !== source) return;
                source.disconnect(); this.source = undefined;
                this.cursor = this.end;
                if (this.state === 'speaking') void this.advance();
            };
            source.start();
            this.setState('speaking');
            this.chunk(this.item!, this.cursor, this.end - this.cursor);
        } catch { if (version === this.version) this.stop('failed'); }
    }
}
