plugins {
    kotlin("jvm") version "2.4.21"
    application
}

repositories { mavenCentral() }

kotlin {
    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11) }
}
tasks.withType<JavaCompile>().configureEach {
    sourceCompatibility = "11"
    targetCompatibility = "11"
}
application { mainClass.set("sample.MainKt") }
