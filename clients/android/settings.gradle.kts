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

// The client talks to the versioned app services of the backend (/api/v1). It signs in as a
// paired device with a device token, never with a password, an API key or a browser cookie.
include(
    ":core-api",
    ":core-design",
    ":core-model",
)
