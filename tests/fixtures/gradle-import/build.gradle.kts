plugins { kotlin("jvm") version "2.4.21" apply false }
subprojects { repositories { mavenCentral() } }

// A local-only missing dependency verifies failure preservation without a network timeout.
if (providers.gradleProperty("vistudioBroken").isPresent) {
    repositories { maven { url = uri("missing-repository") } }
    val brokenRuntimeClasspath by configurations.creating { isCanBeResolved = true; isCanBeConsumed = false }
    dependencies { add(brokenRuntimeClasspath.name, "vistudio.fixture:missing:1.0") }
}
