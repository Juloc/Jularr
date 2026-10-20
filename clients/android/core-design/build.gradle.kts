plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "de.juloc.jularr.core.design"
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
    testImplementation(libs.junit)
    // The Android stub of org.json throws in unit tests; the real implementation runs there.
    testImplementation(libs.org.json)
}
