pluginManagement {
    repositories {
        gradlePluginPortal()
        mavenCentral()
        google()
    }
    plugins {
        kotlin("multiplatform") version "2.4.21"
        id("org.jetbrains.kotlin.plugin.compose") version "2.4.21"
        id("org.jetbrains.compose") version "1.12.2"
    }
}

dependencyResolutionManagement {
    repositories {
        mavenCentral()
        google()
    }
}

rootProject.name = "vistudio-compose-multiplatform"
include(":composeApp", ":core")
