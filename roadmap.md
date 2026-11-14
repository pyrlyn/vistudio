# Approved future work

The initial VSIX, Gradle compiler Error List, and real Kotlin LSP navigation phases are complete. No further implementation phase is scheduled. The broader architecture also describes IDE project-model integration and JVM debugging; selecting another implementation phase requires a follow-up request.

## Mixed-language support (creator-approved, phased)

Phase 1 (Java, T30) is in `plan.md`. Each later phase pairs Kotlin with the target's own language in one debug session. Debug adapters are user-installed and configured by path in Tools > Options (creator decision); the VSIX does not bundle them. These phases build on T21's targets and startup target.

- T31. Kotlin/JS with JavaScript on Node: JS coloring and IntelliSense come from Visual Studio; a debug session through the user-installed vscode-js-debug DAP server binds breakpoints in `.kt` (via Kotlin/JS source maps) and `.js` files together.
- T32. Kotlin/Wasm on Node: the same js-debug path for the `wasmJs` Node target, with Kotlin breakpoints through the generated source maps and the JS glue in the same session.
- T33. Kotlin/Native with C on Windows (`mingwX64`): the binary carries DWARF, not PDB, so debugging uses the user-installed LLVM `lldb-dap`; Kotlin and cinterop C/C++ sources share one session. C/C++ coloring and IntelliSense come from Visual Studio.

## Editor scroll bar

- T41. Failed-test marks in the scroll bar map: after T34 records test results, mark failed tests at their source lines with a Vistudio overview mark (own color in Fonts and Colors), next to T40's map mode settings. Creator-approved with T40.
