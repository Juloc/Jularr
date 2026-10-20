# Optional Speech runtime: source and changes

The linked WASM runtime is distributed under GPLv3 because it includes eSpeak NG.
Original Apache/MIT/public-domain notice terms are retained (only trailing
whitespace/line endings normalized), not replaced by a blanket
"non-commercial" statement. Model weights are separate opt-in inputs and keep
their own catalog terms; none is copied into this directory.

## Pinned sources

- [sherpa-onnx 1.13.8, 11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf](https://github.com/k2-fsa/sherpa-onnx/tree/11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf), Apache-2.0. Its CMake files pin and hash the transitive sources, including kaldi-native-fbank 1.22.3, kaldi-decoder 0.3.0, simple-sentencepiece 0.7 and nlohmann/json 3.12.0.
- [eSpeak NG ed530aa113046142eb5115cf2fc9157854d0ffe1](https://github.com/csukuangfj/espeak-ng/tree/ed530aa113046142eb5115cf2fc9157854d0ffe1), GPLv3; archive SHA-256 `e4e262cbe34f7fe21f91f1ba3397f2728e1f30eafbae7853f2b753a9ed13f0dd`.
- [piper-phonemize f3ff95afc03640bc1399e113e83361192a2fafb4](https://github.com/csukuangfj/piper-phonemize/tree/f3ff95afc03640bc1399e113e83361192a2fafb4), MIT, with its retained uni-algo public-domain/MIT notices. Archive SHA-256 `d9cca4e2bdc7d6dd8dffb96a4668283dbd3f77a9c194a3e530c1e8eba9406a5d`.
- [ONNX Runtime 1.28.2](https://github.com/microsoft/onnxruntime/tree/v1.28.2), MIT with its original ThirdPartyNotices. The CPU/SIMD static input is [onnxruntime-wasm-static_lib-simd-1.28.2.zip](https://github.com/csukuangfj/onnxruntime-libs/releases/download/v1.28.2/onnxruntime-wasm-static_lib-simd-1.28.2.zip), SHA-256 `88688849b67fed9ca0e9fb2bf92ce19a5def9e8c2c8c2df34eb235d25893232a`; the [source build script](https://github.com/csukuangfj/onnxruntime-build/blob/13629865952bf83e6fd81748464a2ed31532ee3a/build-wasm-static_lib.sh) checks out the version tag, recursively obtains its pinned submodules and disables LTO as documented there. No CUDA/proprietary execution provider is enabled here.
- [Emscripten 4.0.23](https://github.com/emscripten-core/emscripten/tree/4.0.23), MIT/NCSA compiler/runtime notices. The reviewed x64 build image digest is `emscripten/emsdk@sha256:835a0e39f40a84d0b754d946d70c2da62239dad2c9cd5d9316f246a12f399f42`.
- [seek-bzip 2.0.0](https://github.com/cscott/seek-bzip/tree/v2.0.0), MIT; npm archive integrity `sha512-SMguiTnYrhpLdk3PwfzHeotrcwi8bNV4iemL9tx9poR/yeaMYwB9VzR1w7b57DuWpuqR8n6oZboi0hj3AxZxQg==`. Only its four core modules are ported; no CLI or commander dependency is included. Their original headers and LICENSE are retained.

## Rebuild and modifications

Run [build-speech-runtime.sh](../../../Tools/build-speech-runtime.sh) explicitly
inside the pinned x64 Emscripten image; normal app/CI builds do not run Docker,
download models or compile C++. It retains its temporary build directory for
inspection and reports the resulting paths/hashes. The source tarball SHA-256
is `0a8db6c55dd318f4a688faba85f7760b99a6c92e8ef8864479d418531bee1ac2`.

Only `INITIAL_MEMORY=512MB` becomes `128MB`; growth stays enabled. This is an
initial allocation, not a promise that every model stays within 128 MiB.
The preloaded `.data` is pronunciation data and a reference token table taken
unchanged from the explicitly fetched, hash-verified Thorsten package, not ONNX
weights. The exact package input is recorded in the build script. The eSpeak
dictionary/rule sources are in the pinned eSpeak tree above. Runtime files mount
the separately verified selected model and tokens under fixed `/voice*` paths.

[prepare-speech-runtime.mjs](../../../Tools/prepare-speech-runtime.mjs) performs
the recorded mechanical browser conversion: scope Emscripten's generated Module
inside an explicit ESM loader, export `createOfflineTts`, and replace seek-bzip's
Node Buffer/CommonJS packaging with Uint8Array/ESM. Decompression, CRC checks,
inference, phonemization and model precision are not changed. Modifying/rebuilding
these GPL-covered components is permitted; maintainers update the reviewed input
hashes when adopting such a changed build rather than bypassing verification.

Reviewed raw WASM SHA-256:
`667780ecc39e93c38b310fac8748e239fd280a1d067e39f6f8149c299f722347`.
The runtime JS/data hashes are recorded in the preparation tool. All application
worker/session/cache code is provided as preferred TypeScript source beside this
file's directory, under [the browser-client license](../../../BROWSER-LICENSE.md).
Before distributing a build, retain accessible matching source revisions and
the full original notices, not just these summaries.
