plugins { kotlin("multiplatform") version "2.4.21" }

repositories { mavenCentral() }

kotlin {
    jvm {
        compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11) }
        mainRun { mainClass.set("sample.MainKt") }
    }
    mingwX64 { binaries.executable { entryPoint = "sample.main" } }
    js { nodejs(); binaries.executable() }
    wasmJs { nodejs(); binaries.executable() }
}

// Executable self-tests exercise shared code on the actual target runtime without a test library.
tasks.register("verifyJvm") { group = "verification"; dependsOn("jvmRun") }
tasks.register("verifyNative") { group = "verification"; dependsOn("runDebugExecutableMingwX64") }
tasks.register("verifyJs") { group = "verification"; dependsOn("jsNodeDevelopmentRun") }
tasks.register("verifyWasm") { group = "verification"; dependsOn("wasmJsNodeDevelopmentRun") }
tasks.register("verifyTargets") {
    group = "verification"
    dependsOn("verifyJvm", "verifyNative", "verifyJs", "verifyWasm")
}
