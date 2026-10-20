#!/usr/bin/env bash
set -euo pipefail

# Maintainer-only build, inside the pinned Emscripten x64 image. Nothing here runs on app startup.
task_output=${1:?Pass an absolute output directory for the four raw runtime artifacts}
case "$task_output" in /*) ;; *) printf 'Output must be absolute.\n' >&2; exit 1 ;; esac
if [[ -d "$task_output" ]] && [[ -n $(find "$task_output" -mindepth 1 -maxdepth 1 -print -quit) ]]; then printf 'Output must be empty.\n' >&2; exit 1; fi
emcc --version | grep -F '4.0.23' >/dev/null
task_work=$(mktemp -d /tmp/jularr-speech.XXXXXXXX)
printf 'Build inputs and logs remain in %s\n' "$task_work"
cd "$task_work"
curl --fail --location --connect-timeout 20 --max-time 180 --output source.tar.gz https://codeload.github.com/k2-fsa/sherpa-onnx/tar.gz/11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf
printf '%s  source.tar.gz\n' 0a8db6c55dd318f4a688faba85f7760b99a6c92e8ef8864479d418531bee1ac2 | sha256sum --check
mkdir source build
tar -xzf source.tar.gz --strip-components=1 -C source
curl --fail --location --connect-timeout 20 --max-time 180 --output pronunciation.tar.bz2 https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-thorsten-medium.tar.bz2
printf '%s  pronunciation.tar.bz2\n' 50487d9c95fdf2191f31d2588569381063ba1591dcd4c7d4bdd30f12b2191714 | sha256sum --check
tar -xjf pronunciation.tar.bz2 -C source/wasm/tts/assets --strip-components=1 --wildcards 'vits-piper-de_DE-thorsten-medium/espeak-ng-data/*' 'vits-piper-de_DE-thorsten-medium/tokens.txt'
if find source/wasm/tts/assets -name '*.onnx' -print -quit | grep -q .; then printf 'Model weights must not be preloaded.\n' >&2; exit 1; fi
sed -i 's/INITIAL_MEMORY=512MB/INITIAL_MEMORY=128MB/' source/wasm/tts/CMakeLists.txt
export SHERPA_ONNX_IS_USING_BUILD_WASM_SH=ON
cmake -S source -B build \
    -DCMAKE_TOOLCHAIN_FILE=/emsdk/upstream/emscripten/cmake/Modules/Platform/Emscripten.cmake \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF \
    -DSHERPA_ONNX_ENABLE_PYTHON=OFF -DSHERPA_ONNX_ENABLE_TESTS=OFF -DSHERPA_ONNX_ENABLE_CHECK=OFF \
    -DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF -DSHERPA_ONNX_ENABLE_JNI=OFF -DSHERPA_ONNX_ENABLE_C_API=ON \
    -DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF -DSHERPA_ONNX_ENABLE_GPU=OFF \
    -DSHERPA_ONNX_ENABLE_WASM=ON -DSHERPA_ONNX_ENABLE_WASM_TTS=ON \
    -DSHERPA_ONNX_ENABLE_BINARY=OFF -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF \
    -DSHERPA_ONNX_LINK_LIBSTDCPP_STATICALLY=OFF
cmake --build build --target sherpa-onnx-wasm-main-tts -j2
mkdir -p "$task_output"
cp build/bin/sherpa-onnx-wasm-main-tts.{js,wasm,data} "$task_output/"
cp source/wasm/tts/sherpa-onnx-tts.js "$task_output/"
sha256sum "$task_output/"*
