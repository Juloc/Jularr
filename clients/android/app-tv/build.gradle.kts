plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.compose.compiler)
}

val releaseVersion = providers.gradleProperty("jularrVersion").orNull
val releaseVersionCode = releaseVersion?.let {
    val numbers = it.substringBefore('-').split('.').map(String::toInt)
    require(numbers.size == 3 && numbers[0] in 0..2000 && numbers[1] in 0..999 && numbers[2] in 0..999) {
        "Android release requires MAJOR.MINOR.PATCH, with a bounded versionCode."
    }
    (numbers[0] * 1_000_000 + numbers[1] * 1_000 + numbers[2]).coerceAtLeast(1)
}
val releaseKeyFile = providers.environmentVariable("JULARR_ANDROID_KEYSTORE").orNull

android {
    namespace = "de.juloc.jularr.tv"
    compileSdk = 36

    defaultConfig {
        applicationId = "de.juloc.jularr.tv"
        minSdk = 26
        targetSdk = 36
        versionCode = releaseVersionCode ?: 1
        versionName = releaseVersion ?: "0.1.0-dev"
    }

    signingConfigs {
        if (!releaseKeyFile.isNullOrBlank()) {
            create("production") {
                storeFile = file(releaseKeyFile)
                storePassword = providers.environmentVariable("JULARR_ANDROID_STORE_PASSWORD").get()
                keyAlias = providers.environmentVariable("JULARR_ANDROID_KEY_ALIAS").get()
                keyPassword = providers.environmentVariable("JULARR_ANDROID_KEY_PASSWORD").get()
            }
        }
    }

    buildTypes {
        getByName("release") {
            if (!releaseKeyFile.isNullOrBlank()) {
                signingConfig = signingConfigs.getByName("production")
            }
        }
    }

    buildFeatures {
        compose = true
        // Self-update (#490) compares BuildConfig.VERSION_NAME against the latest GitHub release.
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation(project(":core-api"))
    implementation(project(":core-model"))
    implementation(project(":core-player"))
    implementation(project(":core-design"))
    implementation(project(":core-session"))
    implementation(project(":core-update"))

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.foundation)
    implementation("androidx.compose.material:material")
    implementation("androidx.compose.material:material-icons-extended")
    implementation(libs.androidx.tv.material)
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation("io.coil-kt.coil3:coil-compose:3.5.0")
    implementation("io.coil-kt.coil3:coil-network-okhttp:3.5.0")
    implementation("com.google.zxing:core:3.5.4")
    debugImplementation(libs.androidx.compose.ui.tooling)
    testImplementation(libs.junit)
    testImplementation(libs.org.json)
}
