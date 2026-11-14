# Kotlin and Java sample

A Gradle Kotlin/JVM application with Java sources in the same module. Kotlin calls Java (`Ledger`) and Java calls Kotlin (`MixedMainKt.doubleIt`). Two entry points: `mixed.MixedMainKt` (Kotlin) and `mixed.JavaEntry` (Java).

Build and run with the project's wrapper and a JDK 11:

```bash
gradlew.bat build run --console=plain
```

Expected output: `total=11 Kotlin:11`.

In Visual Studio, open this folder, copy `launch.vs.json` into `.vs`, set breakpoints in `MixedMain.kt` and `Ledger.java`, and press F5. Both entry files also start with F5 on **Current Document**.

Repeatable checks from the repository root:

- `python -I tests/integration/verify-java-mixed.py` — real Kotlin server: Java-aware Kotlin completion/navigation and one debug session across both languages (breakpoints, stepping, call stack, locals, evaluation, both entry points).
- `python -I tests/integration/verify-java-lsp.py <jdtls folder> <java 21 home> <gradle jdk>` — Eclipse JDT LS started like the extension does: Java completion, hover, definition, Kotlin symbols resolved from Java, no Eclipse files in this folder.
