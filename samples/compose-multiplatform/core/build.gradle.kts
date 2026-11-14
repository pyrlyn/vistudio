// Plain Kotlin Multiplatform module without Compose: the preview window reports that it has no Compose previews.
plugins { kotlin("multiplatform") }

kotlin {
    jvm("desktop")
    @OptIn(org.jetbrains.kotlin.gradle.ExperimentalWasmDsl::class)
    wasmJs { browser() }
}
