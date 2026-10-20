import Bunzip from './Runtime/seek-bzip/index.js';

export const ArchiveLimit = 128 * 1024 * 1024;
export const ExpandedLimit = 384 * 1024 * 1024;
const ModelLimit = 192 * 1024 * 1024;
const TokenLimit = 1024 * 1024;

// Consume tar as the decoder writes it: only the two needed regular files are retained.
// No filesystem paths from the package are passed to Emscripten.
export class ModelTar {
    private header = new Uint8Array(512);
    private position = 0;
    private total = 0;
    private remaining = 0;
    private padding = 0;
    private zeroBlocks = 0;
    private target?: Uint8Array;
    private longName?: string;
    private metadata = false;
    private offset = 0;
    model?: Uint8Array;
    tokens?: Uint8Array;

    writeByte = (value: number): void => {
        if (++this.total > ExpandedLimit) throw new Error('Expanded speech archive exceeds its limit.');
        if (this.remaining) {
            if (this.target) this.target[this.offset++] = value;
            if (--this.remaining === 0 && this.metadata) {
                this.longName = this.text(this.target!);
                this.metadata = false;
            }
            return;
        }
        if (this.padding) { if (value !== 0) throw new Error('Invalid tar padding.'); this.padding--; return; }
        this.header[this.position++] = value;
        if (this.position !== 512) return;
        this.position = 0;
        if (this.header.every(byte => byte === 0)) { this.zeroBlocks++; return; }
        if (this.zeroBlocks) throw new Error('Data after tar terminator.');
        const field = (start: number, length: number) => this.text(this.header.subarray(start, start + length));
        const octal = (value: string) => {
            const number = value.trim();
            if (!/^[0-7]+$/.test(number)) throw new Error('Invalid tar size/checksum.');
            const result = parseInt(number, 8);
            if (!Number.isSafeInteger(result)) throw new Error('Invalid tar number.');
            return result;
        };
        const checksum = this.header.reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
        if (octal(field(148, 8)) !== checksum) throw new Error('Invalid tar checksum.');
        const size = octal(field(124, 12));
        if (size > ModelLimit) throw new Error('Speech archive entry exceeds its limit.');
        this.remaining = size;
        this.padding = (512 - size % 512) % 512;
        this.target = undefined;
        this.offset = 0;
        const type = field(156, 1);
        if (type === 'L') {
            if (!size || size > 4096 || this.longName) throw new Error('Invalid long tar name.');
            this.metadata = true;
            this.target = new Uint8Array(size);
            return;
        }
        const prefix = field(345, 155);
        const name = this.longName ?? (prefix ? prefix + '/' : '') + field(0, 100);
        this.longName = undefined;
        if (!name || name.startsWith('/') || name.includes('\\') || name.includes(':') || name.replace(/\/$/, '').split('/').some(part => !part || part === '.' || part === '..')) {
            throw new Error('Unsafe speech archive path.');
        }
        if (type === '5' && size === 0) return;
        if (type !== '' && type !== '0') throw new Error('Unsupported speech archive entry.');
        if (name.endsWith('.onnx')) {
            if (this.model || !size) throw new Error('Expected exactly one speech model.');
            this.target = this.model = new Uint8Array(size);
        } else if (name.split('/').at(-1) === 'tokens.txt') {
            if (this.tokens || !size || size > TokenLimit) throw new Error('Invalid speech tokens.');
            this.target = this.tokens = new Uint8Array(size);
        }
    };

    finish(): { model: Uint8Array; tokens: Uint8Array } {
        if (this.position || this.remaining || this.padding || this.longName || this.zeroBlocks < 2 || !this.model || !this.tokens) throw new Error('Incomplete speech archive.');
        new TextDecoder('utf-8', { fatal: true }).decode(this.tokens);
        return { model: this.model, tokens: this.tokens };
    }

    private text(bytes: Uint8Array): string {
        const end = bytes.indexOf(0);
        return new TextDecoder('utf-8', { fatal: true }).decode(end < 0 ? bytes : bytes.subarray(0, end));
    }
}

export async function verifyArchive(bytes: ArrayBuffer, size: number, hash: string): Promise<void> {
    if (!Number.isSafeInteger(size) || size <= 0 || size > ArchiveLimit || bytes.byteLength !== size || !/^[a-f0-9]{64}$/.test(hash)) throw new Error('Invalid speech archive size/hash.');
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    if (Array.from(digest, value => value.toString(16).padStart(2, '0')).join('') !== hash) throw new Error('Speech archive integrity check failed.');
}

export function unpackModel(archive: ArrayBuffer): { model: Uint8Array; tokens: Uint8Array } {
    if (!archive.byteLength || archive.byteLength > ArchiveLimit) throw new Error('Speech archive exceeds its limit.');
    const bytes = new Uint8Array(archive);
    const tar = new ModelTar();
    let position = 0;
    const input = {
        readByte() { if (position >= bytes.length) throw new Error('Truncated bzip2 stream.'); return bytes[position++]; },
        read(buffer: Uint8Array, offset: number, count: number) { for (let index = 0; index < count; index++) buffer[offset + index] = this.readByte(); return count; }
    };
    Bunzip.decode(input, tar, false);
    if (position !== bytes.length) throw new Error('Trailing compressed speech data.');
    return tar.finish();
}
