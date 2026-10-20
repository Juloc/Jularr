`speech-model.tar.bz2` is a synthetic, non-inference fixture. Its `voice.onnx`
and `tokens.txt` both contain the seek-bzip MIT license, including attribution.
It contains no neural weights and cannot speak. Archive verification/decompression
and device storage are real in the browser test; inference there is explicitly
doubled. Real-model/audio checks use `Tools/speech-neural-smoke.mjs` separately.
