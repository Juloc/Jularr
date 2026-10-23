import loadBergamot, { type BergamotModule } from "./bergamot-translator.js";

// Translates plain texts with Mozilla's Bergamot engine; one model, or two through English.
interface ModelFiles { precision: string; source: string; target: string; files: Record<string, string> }
type Request = { type: "load"; engine: string; models: ModelFiles[] } | { type: "translate"; id: number; texts: string[] };

// Memory alignment Bergamot expects per file type.
const Alignments: Record<string, number> = { model: 256, lex: 64, vocab: 64, srcvocab: 64, trgvocab: 64 };
let engine: { bergamot: BergamotModule; service: InstanceType<BergamotModule["BlockingService"]>; models: unknown[] } | undefined;

self.addEventListener("message", async (event: MessageEvent<Request>) => {
    const request = event.data;
    try {
        if (request.type === "load") {
            engine = await load(request.engine, request.models);
            self.postMessage({ type: "ready" });
            return;
        }

        self.postMessage({ type: "translated", id: request.id, texts: translate(request.texts) });
    } catch (error) {
        self.postMessage({ type: "failed", id: request.type === "translate" ? request.id : undefined, message: String(error) });
    }
});

async function load(engineUrl: string, models: ModelFiles[]) {
    const wasmBinary = await bytes(engineUrl);
    const bergamot = await new Promise<BergamotModule>((resolve, reject) => {
        const module: BergamotModule = loadBergamot({
            INITIAL_MEMORY: 234_291_200,
            print: () => undefined,
            onAbort: () => reject(new Error("Bergamot could not start.")),
            onRuntimeInitialized: () => queueMicrotask(() => resolve(module)),
            wasmBinary
        });
    });

    const built = [];
    for (const model of models) {
        const memory: Record<string, unknown> = {};
        for (const [type, url] of Object.entries(model.files)) {
            const data = new Uint8Array(await bytes(url));
            const aligned = new bergamot.AlignedMemory(data.byteLength, Alignments[type] ?? 64);
            aligned.getByteArrayView().set(data);
            memory[type] = aligned;
        }

        const vocabularies = new bergamot.AlignedMemoryList();
        if (memory.vocab) vocabularies.push_back(memory.vocab);
        else { vocabularies.push_back(memory.srcvocab); vocabularies.push_back(memory.trgvocab); }
        built.push(new bergamot.TranslationModel(model.source, model.target, config(model.precision), memory.model, memory.lex ?? null, vocabularies, null));
    }

    return { bergamot, service: new bergamot.BlockingService({ cacheSize: 0 }), models: built };
}

function translate(texts: string[]): string[] {
    if (!engine) throw new Error("Bergamot is not loaded.");
    const { bergamot, service, models } = engine;
    const messages = new bergamot.VectorString();
    const options = new bergamot.VectorResponseOptions();
    const indexes: number[] = [];
    texts.forEach((text, index) => {
        // Empty texts break the engine and stay empty.
        if (text.trim().length === 0) return;
        messages.push_back(text);
        options.push_back({ qualityScores: false, alignment: false, html: false });
        indexes.push(index);
    });

    const result = texts.map(() => "");
    if (indexes.length === 0) return result;
    const responses = models.length === 1 ? service.translate(models[0], messages, options) : service.translateViaPivoting(models[0], models[1], messages, options);
    indexes.forEach((index, position) => { result[index] = responses.get(position).getTranslatedText(); });
    messages.delete();
    options.delete();
    responses.delete();
    return result;
}

async function bytes(url: string): Promise<ArrayBuffer> {
    const response = await fetch(url, { credentials: "same-origin" });
    if (!response.ok) throw new Error(`${url} answered ${response.status}`);
    return response.arrayBuffer();
}

// Firefox's settings for greedy, fast decoding.
function config(precision: string): string {
    const values: Record<string, string> = {
        "beam-size": "1", normalize: "1.0", "word-penalty": "0", "max-length-break": "128", "mini-batch-words": "1024", workspace: "128",
        "max-length-factor": "2.0", "skip-cost": "true", "cpu-threads": "0", quiet: "true", "quiet-translation": "true", "gemm-precision": precision, alignment: "soft"
    };
    const indent = "            ";
    return "\n" + Object.entries(values).map(([key, value]) => `${indent}${key}: ${value}\n`).join("") + indent;
}
