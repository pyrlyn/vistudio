# Compose Multiplatform sample

Sample for the Kotlin Compose Preview window.

- `:composeApp` — Compose Multiplatform 1.12.2 with `desktop` (JVM) and `wasmJs` targets. `src/commonMain/kotlin/sample/ui/Cards.kt` has three previews: a wrap-content card, a fixed-size badge with a background, and one that throws on purpose.
  `src/commonMain/kotlin/sample/ui/Interactive.kt` has three previews for the window's **Interactive** mode: a button that counts clicks, a text field, and a square that changes color by itself.
- `:core` — plain Kotlin Multiplatform module without Compose, used by the card.

The desktop entry point is `sample.MainKt` in `desktopMain` (Compose `application` block).

To preview it in Visual Studio, open this folder, run **Tools > Kotlin > Compose Preview** and open `Cards.kt`. The first render downloads and compiles the desktop target; later renders reuse the Gradle build.
