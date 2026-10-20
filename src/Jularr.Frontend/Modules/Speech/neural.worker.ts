import load, { type SherpaModule } from './Runtime/sherpa-onnx-wasm-main-tts.js';
import { createOfflineTts } from './Runtime/sherpa-onnx-tts.js';
import wasm from './Runtime/sherpa-onnx-wasm-main-tts.wasm?url';
import data from './Runtime/sherpa-onnx-wasm-main-tts.data?url';
import { unpackModel, verifyArchive } from './ModelArchive';
import type { ModelFiles, SpeechModel } from './ModelStore';

type Request = { type: 'unpack'; archive: ArrayBuffer; model: SpeechModel } | { type: 'load'; files: ModelFiles }
    | { type: 'generate'; id: number; text: string; speed: number; voice: number };
let engine: ReturnType<typeof createOfflineTts> | undefined;
let module: SherpaModule;

self.onmessage = async (event: MessageEvent<Request>) => {
    const request = event.data;
    try {
        if (request.type === 'unpack') {
            await verifyArchive(request.archive, request.model.sizeBytes, request.model.sha256);
            const files = unpackModel(request.archive);
            self.postMessage({ type: 'unpacked', files: { model: files.model.buffer, tokens: files.tokens.buffer } },
                { transfer: [files.model.buffer, files.tokens.buffer] });
        } else if (request.type === 'load') {
            if (engine) throw new Error('Speech engine is already loaded.');
            module = await new Promise<SherpaModule>((resolve, reject) => {
                const instance = load({ locateFile: path => path.endsWith('.wasm') ? wasm : data, print: () => {}, printErr: () => {},
                    onAbort: error => reject(new Error(String(error))), onRuntimeInitialized: () => queueMicrotask(() => resolve(instance)) });
            });
            module.FS_createDataFile('/voice.onnx', null, new Uint8Array(request.files.model), true, false, true);
            module.FS_createDataFile('/voice-tokens.txt', null, new Uint8Array(request.files.tokens), true, false, true);
            engine = createOfflineTts(module, { offlineTtsModelConfig: { offlineTtsVitsModelConfig: {
                model: '/voice.onnx', tokens: '/voice-tokens.txt', dataDir: '/espeak-ng-data', noiseScale: 0.667, noiseScaleW: 0.8, lengthScale: 1
            }, numThreads: 1, debug: 0, provider: 'cpu' }, maxNumSentences: 1 });
            if (!engine.numSpeakers || !engine.sampleRate) throw new Error('Unsupported speech model.');
            self.postMessage({ type: 'ready' });
        } else {
            if (!engine || !request.text.trim() || request.text.length > 240 || !Number.isFinite(request.speed) || request.speed < .5 || request.speed > 2.5
                || !Number.isInteger(request.voice) || request.voice < 0 || request.voice >= engine.numSpeakers) throw new Error('Invalid speech generation request.');
            const result = engine.generate({ text: request.text, sid: request.voice, speed: request.speed });
            if (!(result.samples instanceof Float32Array) || !result.samples.length || result.samples.length > engine.sampleRate * 120) throw new Error('Invalid speech audio.');
            self.postMessage({ type: 'audio', id: request.id, samples: result.samples, sampleRate: engine.sampleRate }, { transfer: [result.samples.buffer] });
        }
    } catch (error) { self.postMessage({ type: 'failed', message: String(error) }); }
};
