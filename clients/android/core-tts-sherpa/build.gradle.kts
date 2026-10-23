plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "de.juloc.jularr.core.tts.sherpa"
    compileSdk = 36

    defaultConfig {
        minSdk = 26
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    api(project(":core-tts"))

    // Manual step (docs/TTS.md, Phase 3): download the sherpa-onnx Android release AAR
    // (currently `sherpa-onnx-<version>.aar`, e.g. from
    // https://github.com/k2-fsa/sherpa-onnx/releases) and place it, unmodified, at
    // core-tts-sherpa/libs/sherpa-onnx.aar. It is not committed to this repository: this
    // module is only included in the Gradle build at all when
    // -PjularrNeuralTtsEnabled=true is passed (see settings.gradle.kts), so the absence
    // of the AAR never affects the default build, CI, or app-mobile/app-tv.
    implementation(files("libs/sherpa-onnx.aar"))
}
