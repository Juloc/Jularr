plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "de.juloc.jularr.core.player"
    compileSdk = 36

    defaultConfig {
        minSdk = 26
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
    }
}


dependencies {
    api(project(":core-model"))
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.media3.exoplayer.hls)
    implementation(libs.androidx.media3.session)
    testImplementation(libs.junit)
}
