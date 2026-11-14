package sample

/** Implemented per target: jvmMain, mingwX64Main, and jsMain/wasmJsMain through the shared webMain source set. */
expect fun platformName(): String
