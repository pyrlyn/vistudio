plugins { kotlin("jvm"); application }
dependencies {
    implementation(project(":shared"))
    // Requested deliberately differs from the selected version; no plugin/toolchain pins are changed.
    implementation("org.jetbrains.kotlin:kotlin-stdlib:2.4.20")
}
configurations.configureEach {
    resolutionStrategy.force("org.jetbrains.kotlin:kotlin-stdlib:2.4.21")
}
application { mainClass.set("sample.MainKt") }
