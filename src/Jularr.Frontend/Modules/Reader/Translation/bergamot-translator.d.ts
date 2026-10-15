// Types for the Emscripten loader of Mozilla's Bergamot engine (MPL-2.0, see LICENSE beside it).
export default function loadBergamot(module: object): BergamotModule;

export interface BergamotVector<T> {
    size(): number;
    get(index: number): T;
    push_back(value: T): void;
    delete(): void;
}

export interface BergamotModule {
    AlignedMemory: new (size: number, alignment: number) => { getByteArrayView(): Uint8Array };
    AlignedMemoryList: new () => BergamotVector<unknown>;
    TranslationModel: new (source: string, target: string, config: string, model: unknown, lex: unknown, vocabularies: unknown, quality: unknown) => unknown;
    BlockingService: new (options: { cacheSize: number }) => {
        translate(model: unknown, messages: BergamotVector<string>, options: BergamotVector<object>): BergamotVector<{ getTranslatedText(): string }>;
        translateViaPivoting(first: unknown, second: unknown, messages: BergamotVector<string>, options: BergamotVector<object>): BergamotVector<{ getTranslatedText(): string }>;
    };
    VectorString: new () => BergamotVector<string>;
    VectorResponseOptions: new () => BergamotVector<object>;
}
