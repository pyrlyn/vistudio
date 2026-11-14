// Load the Kotlin and Compose plugins once for all subprojects; each module applies what it needs.
plugins {
    kotlin("multiplatform") apply false
    id("org.jetbrains.kotlin.plugin.compose") apply false
    id("org.jetbrains.compose") apply false
}
