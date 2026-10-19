plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "de.juloc.jularr.core.api"
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
    api(project(":core-model"))
    implementation(libs.kotlinx.coroutines.core)
    testImplementation(libs.junit)
    // The Android stub of org.json throws in unit tests; the real implementation runs there.
    testImplementation(libs.org.json)
}
