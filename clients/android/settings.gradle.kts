pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "JularrAndroid"

include(
    ":app-mobile",
    ":app-tv",
    ":core-api",
    ":core-model",
    ":core-player",
    ":core-session",
    ":core-design",
    ":core-tts",
    ":core-update",
)

// The sherpa-onnx offline-neural TTS runtime is not published to Maven Central (see
// k2-fsa/sherpa-onnx#3981), only as a manually downloaded AAR release asset. This module
// stays out of the default build (and CI, which has no AAR) so nothing else is affected;
// opt in locally with -PjularrNeuralTtsEnabled=true after following the manual step in
// docs/TTS.md.
val neuralTtsEnabled = providers.gradleProperty("jularrNeuralTtsEnabled")
    .getOrElse("false")
    .toBoolean()

if (neuralTtsEnabled) {
    include(":core-tts-sherpa")
}
