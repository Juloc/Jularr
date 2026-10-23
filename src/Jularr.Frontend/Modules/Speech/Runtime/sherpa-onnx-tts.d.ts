import type { SherpaModule } from './sherpa-onnx-wasm-main-tts.js';
export function createOfflineTts(module: SherpaModule, configuration: object): {
    numSpeakers: number;
    sampleRate: number;
    generate(options: { text: string; sid: number; speed: number }): { samples: Float32Array };
};
