package sample

/** Shared by the JS and Wasm targets through the default webMain source set. */
internal fun webPlatform(engine: String): String = "Web/$engine"
