plugins {
    kotlin("multiplatform")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.compose")
}

val composeVersion = "1.12.2"

kotlin {
    jvm("desktop")
    @OptIn(org.jetbrains.kotlin.gradle.ExperimentalWasmDsl::class)
    wasmJs {
        browser()
        binaries.executable()
    }

    sourceSets {
        commonMain.dependencies {
            implementation(project(":core"))
            implementation("org.jetbrains.compose.runtime:runtime:$composeVersion")
            implementation("org.jetbrains.compose.foundation:foundation:$composeVersion")
            implementation("org.jetbrains.compose.ui:ui:$composeVersion")
            implementation("org.jetbrains.compose.ui:ui-tooling-preview:$composeVersion")
        }
        val desktopMain by getting {
            dependencies { implementation(compose.desktop.currentOs) }
        }
    }
}

compose.desktop {
    application { mainClass = "sample.MainKt" }
}
