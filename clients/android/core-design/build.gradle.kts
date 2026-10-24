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


val playerDesignAssetsDir = layout.projectDirectory.dir("src/main/assets")

tasks.register<Sync>("syncPlayerDesignAssets") {
    from(rootProject.file("../../design/player")) {
        include("player-tokens.json", "player-icons.json", "player-actions.json")
    }
    into(playerDesignAssetsDir)
}

tasks.named("preBuild").configure {
    dependsOn("syncPlayerDesignAssets")
}

dependencies {
    testImplementation(libs.junit)
    // Real org.json for JVM unit tests (android.jar only ships stubs).
    testImplementation(libs.org.json)
}
