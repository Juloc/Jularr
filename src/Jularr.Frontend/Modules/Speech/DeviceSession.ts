export interface SpeechItem { text: string; range: Range }
export interface SpeechOptions { rate: number; pitch: number; volume: number; language: string; voice?: SpeechSynthesisVoice }
export type SpeechState = 'idle' | 'loading' | 'speaking' | 'paused' | 'failed';

export function chunkEnd(text: string, start: number): number {
    let end = Math.min(text.length, start + 240);
    if (end === text.length) return end;
    const sample = text.slice(start, end);
    const stop = Math.max(sample.lastIndexOf('. '), sample.lastIndexOf('! '), sample.lastIndexOf('? '), sample.lastIndexOf('; '));
    const space = sample.lastIndexOf(' ');
    if (stop > 60) end = start + stop + 1;
    else if (space > 60) end = start + space + 1;
    if (/[\uD800-\uDBFF]/.test(text[end - 1])) end--;
    return end;
}

// One live utterance, pulled lazily. Cancellation invalidates every old callback.
// Cancel/resume from the last word also works on platforms whose native pause stalls.
export class DeviceSession {
    state: SpeechState = 'idle';
    private generation = 0;
    private iterator?: Iterator<SpeechItem>;
    private item?: SpeechItem;
    private options?: SpeechOptions;
    private cursor = 0;
    private boundary = 0;

    constructor(private synth: SpeechSynthesis, private utterance: (text: string) => SpeechSynthesisUtterance,
        private changed: (state: SpeechState) => void, private word: (item: SpeechItem, offset: number, length: number) => void) {}

    start(items: Iterable<SpeechItem>, options: SpeechOptions): void {
        this.stop();
        this.iterator = items[Symbol.iterator]();
        this.options = options;
        this.setState('speaking');
        this.advance();
    }

    stop(state: SpeechState = 'idle'): void {
        this.generation++;
        this.iterator = undefined;
        this.item = undefined;
        this.cursor = this.boundary = 0;
        this.synth.cancel();
        this.setState(state);
    }

    pause(): void {
        if (this.state !== 'speaking') return;
        this.generation++;
        this.cursor = this.boundary;
        this.synth.cancel();
        this.setState('paused');
    }

    resume(): void {
        if (this.state !== 'paused') return;
        this.setState('speaking');
        this.advance();
    }

    private setState(state: SpeechState): void { this.state = state; this.changed(state); }

    private advance(): void {
        if (this.state !== 'speaking' || !this.iterator || !this.options) return;
        while (!this.item || this.cursor >= this.item.text.length) {
            const next = this.iterator.next();
            if (next.done) { this.stop(); return; }
            this.item = next.value;
            this.cursor = this.boundary = 0;
            if (!this.item.text.trim()) { this.item = undefined; continue; }
            break;
        }
        const item = this.item, start = this.cursor, end = chunkEnd(item.text, start), generation = ++this.generation;
        this.boundary = start;
        const message = this.utterance(item.text.slice(start, end));
        Object.assign(message, { rate: this.options.rate, pitch: this.options.pitch, volume: this.options.volume, lang: this.options.language, voice: this.options.voice ?? null });
        const current = () => generation === this.generation && this.state === 'speaking';
        const highlight = (at: number, length = 0) => {
            const leading = item.text.slice(at, end).search(/\S/);
            if (leading < 0) return;
            at += leading;
            this.boundary = at;
            this.word(item, at, length > 0 ? length : /^\S+/.exec(item.text.slice(at, end))?.[0].length ?? 1);
        };
        message.onstart = () => { if (current()) highlight(start); };
        message.onboundary = event => { if (current()) highlight(Math.min(end, start + event.charIndex), event.charLength); };
        message.onend = () => { if (current()) { this.cursor = end; this.advance(); } };
        message.onerror = () => { if (current()) this.stop('failed'); };
        try { this.synth.speak(message); }
        catch { if (current()) this.stop('failed'); }
    }
}
