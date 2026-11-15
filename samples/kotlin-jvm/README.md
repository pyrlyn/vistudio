# Kotlin/JVM sample

Open this folder using Visual Studio's File > Open > Folder. Use Tools > Kotlin > Build with Gradle, or run `gradlew.bat build run --console=plain` from a terminal. A compatible JDK is required; the example targets JVM 11. The first build downloads Gradle and Kotlin dependencies.

The Gradle Wrapper launcher and JAR come from Gradle v8.14.7 under the license in gradle/LICENSE. The distribution is checksum-pinned. Wrapper JAR SHA-256: `ad16bca46cb71bb5887c546860d93992aeee35c1fdaf4a242b0a612375d937e0`.

This project has no .NET or MSBuild dependency. Set the external Kotlin language server in the extension's Options page to enable semantic editing and JVM debugging.

## Debug acceptance check

Copy `launch.vs.json` into `.vs/launch.vs.json` (create `.vs` if necessary). This is Visual Studio's Open Folder configuration. Use Kotlin server 263.7084.0 and wait for its Gradle import to finish.

1. Open Main.kt and press F9 on line 5. Select Current Document in the startup dropdown and press F5.
2. Gradle must build successfully before debugging begins. The debugger should stop on line 5 before `println` runs.
3. Open Locals, expand `greeting`, and inspect its `name` field (`Kotlin`). Use Call Stack to inspect the Kotlin source frame.
4. Press F11 to enter `Greeting.message`, then Shift+F11 to return. F10 steps over the current line, and F5 continues execution.
5. Debug Output should contain `Hello, Kotlin from Visual Studio!`. Start again and press Shift+F5 while stopped; the owned target should exit.
6. To test the build gate, temporarily introduce an unresolved reference and press F5. The build must fail with an Error List entry and no target JVM. Undo the edit before continuing.

No Gradle daemon, JDWP port, Java classpath or debugger process needs to be configured manually. The backend derives the runtime model from the imported Gradle module.

## Watch and Immediate acceptance check

The launch template also includes **Kotlin expression checks**, using `sample.EvaluationMainKt`. The original `Main.kt` entry point remains the default Gradle application.

1. Copy the updated launch template into `.vs/launch.vs.json`, open `EvaluationMain.kt`, set a breakpoint on line 8, select **Kotlin expression checks** in the startup dropdown and press F5.
2. In Watch 1, enter `greeting`, `greeting.name.length + 1`, `nullableName ?: "Guest"`, and `greeting.copy(name="Watch")`. Expect `Greeting(name=Kotlin)`, `7`, `"Guest"`, and `Greeting(name=Watch)`. Expand the last object: its `name` field is `"Watch"`.
3. Open Immediate with Ctrl+Alt+I. Enter `? greeting.name.uppercase()` and `? listOf(1, 2, 3).sum()` separately, pressing Enter after each. Expect `"KOTLIN"` and `6`.
4. Enter `? counter.next()` twice. Expect `1`, then `2`; `? counter.calls` must report `2`. Do not add `counter.next()` to Watch because automatic Watch refreshes could intentionally call it again.
5. Enter `? missingExpression` to see the backend diagnostic, then `? 1 + 2` to confirm evaluation still works (`3`).
6. Press F10 to reach line 9 and check the Watch values again. Press F5: Debug Output should contain `Kotlin`, `Evaluation calls: 2; fallback: Guest`, and exit code 0.

These checks use the real Kotlin/JVM evaluator in a suspended frame. They do not imply persistent declarations across Immediate commands.

## Frame completion acceptance check

Use **Kotlin expression checks**, with breakpoints on `EvaluationMain.kt` lines 8 and 15. Remove any side-effecting Watch expressions before this check.

1. At line 8, edit a blank Watch Name row (F2), type `gre`, and invoke Ctrl+Space. Select `greeting` with Tab. Append `.` and select `name`; submit the completed expression. Its value should be `"Kotlin"`.
2. In Immediate, type `? gre`, invoke Ctrl+Space, accept `greeting`, then complete `.na` to `.name`. Enter should evaluate the completed expression to `"Kotlin"`.
3. Continue to line 15 in `completionFrame`. Root suggestions should include `value`, `counter`, and `frameOnly`. Select the caller `main` in Call Stack: root suggestions should now include `args`, `greeting`, `counter`, and `nullableName`. Repeat in both windows; inner-frame names must not leak into the caller's list.
4. Continue to completion. Output should include `Evaluation calls: 0; fallback: Guest`, `Frame locals: Inner frame; Kotlin; calls: 0`, and exit code 0. Completion must not invoke `next()` or `slowNext()`.

This checks native debugger-property completion. The callable checks below additionally exercise semantic LSP completion.

## Method and extension completion without side effects

Use **Kotlin expression checks** and the same line 8/15 breakpoints. Keep side-effecting expressions out of saved Watch entries.

1. At line 8, request completion for `target.bu` in Watch and `? target.bu` in Immediate. Both lists should contain `bumpMethod()` and the imported `bumpExtension()`. Accept one with Tab: only its name should be inserted. Do not append `()` or submit a call; cancel the Watch edit afterward.
2. Request `target.over` and inspect both `overload(value: Int)` and `overload(value: String)` signatures. Request `greeting.name.up` to see standard String extensions.
3. Continue to line 15. The same receiver name `target` now has type `InnerCompletionTarget`: `target.in` should offer `innerMethod()` and `innerExtension()`. Repeat in both native windows.
4. Select caller `main` in Call Stack without continuing. `target.bu` must again offer the outer method/extension; the inner methods must not leak across frame selection.
5. Inspect only `? counter.calls` (a field read); it should remain 0. Cancel added Watch edits, then continue. Output must show `Evaluation calls: 0`, `Frame locals: Inner frame; Kotlin; calls: 0` and exit code 0. All fixture methods/extensions increment the same counter if executed.

For stronger evidence, enable native Debug Adapter Host logging before F5. Compare the request log before/after opening and navigating the lists: completion should add only scopes/variables requests, never `evaluate` for `target` or the suggested callables. Existing saved Watch expressions can refresh legitimately on a frame change. LSP completion opens and closes a virtual document; no `__VistudioDebugCompletion_*.kt` file should appear on disk.

## Local/private extensions and generic completion

Use **Kotlin expression checks**, breakpoints at EvaluationMain.kt lines 8 and 15, and no side-effecting saved Watches.

1. At line 8, request `items.lex` in Watch and `? items.lex` in Immediate with Ctrl+Space. Expect `lexLocalStrings`, `lexPrivateStrings`, and `lexPrivateGeneric` returning `String?`. Neither `lexUnrelatedLocal` nor the other file's `lexForeignPrivate` should appear. Tab inserts a name only; cancel the Watch edit without submitting a call.
2. Request `box.value.get` in Immediate. `get(index: Int)` should return `String`; `box.lex` should offer `lexBox` returning `List<String>`.
3. Continue to line 15. Both windows should now offer `lexLocalInts`, `lexPrivateInts`, and generic result `Int?` for the same `items.lex` text. Selecting `main` in Call Stack without continuing must restore the String candidates in both windows.
4. Read only `? counter.calls`: expect 0. Continue to completion. Output should contain `Evaluation calls: 0`, `Member calls: 0`, `Frame locals: Inner frame; Kotlin; calls: 0`, and exit code 0. All lexical test extensions increment this shared counter if invoked.

The full source snapshot also contains GenericBox's private member extension. To test that separate case, step over lines 8 and 9, then F11 into `box.inspect` at GenericBox.kt line 7 and request `memberItems.lex` in both windows. Expect the private `<U> List<U>.lexMember` to return `List<String>`. Generic parameter `T` remains a source-level `T`; runtime erasure is not reified. The enclosing-class parameter fix has a separate fixture below.

## Enclosing-class generic extension acceptance

Select **Kotlin generic owner completion**, from the updated launch template, and set breakpoints in `OwnerCompletionMain.kt` at lines 19 and 32. Keep side-effecting expressions out of saved Watches.

1. At line 19 in `OuterOwner<T : CharSequence>`, request `value.owner` and `items.owner` in both Watch and Immediate (`?` prefix). Expect `ownerOuterValue` returning `T` and `ownerOuterList` returning `List<T>`. `ownerWrong` must be absent. Cancel edits without submitting calls.
2. Continue to line 32 in `InnerOwner<T : Number>`. Repeat the same prefixes in both windows: expect `ownerInnerValue` and `ownerInnerList`, without outer-owner candidates or `ownerWrong`.
3. Select caller `OuterOwner.pause`, line 20, in Call Stack without continuing. Both lists must restore the outer-owner functions. Switch back and repeat to exercise reused receiver names and generic parameter names.
4. Read only `? counter.calls`: expect 0. Continue; all four printed counter values must be 0, Gradle must succeed, and exit code must be 0. Showing/accepting a name must not add DAP `evaluate` requests. Existing saved Watches may legitimately refresh when the selected frame changes.

These are the native-UI acceptance steps; their execution for this latest fix is still pending. The production C# completion logic passed 12 requests against real LSP/JVM stack frames from this fixture, including repeated caller selection, with a zero counter and no evaluated calls.

## Cold saved-Watch acceptance check

Use the official `intellij-server.exe` launcher so the extension applies its bounded hover timeout adjustment. Keep the breakpoint at `EvaluationMain.kt` line 8 and the **Kotlin expression checks** target selected.

1. In one preparation session, add the four Watch expressions above and `counter.slowNext()`. The latter increments a counter before waiting 750 ms, deliberately crossing the backend's original 500 ms hover limit. Stop debugging and close Visual Studio completely.
2. Reopen the same folder in Visual Studio and press F5. On the first stop, inspect the saved Watch entries without re-entering or refreshing them. The original four values should be resolved and `counter.slowNext()` should show `1`.
3. In Immediate, evaluate only `? counter.calls`: it must be `1`. Do not invoke `next()` or `slowNext()` manually in this run. Continue directly with F5, without stepping into another suspension: Debug Output should show `Evaluation calls: 1; fallback: Guest` and exit code 0.
4. Remove the test's `counter.slowNext()` Watch entry afterward. A Watch expression may legitimately execute again in another suspension; do not leave side-effecting test expressions in ordinary debugging sessions.

For protocol evidence, enable `DebugAdapterHost.Logging /On:<absolute-log-path>` in Visual Studio's Command Window before the first F5. At the first stop, the saved expression should have one `evaluate` request with `context: hover`, `noSideEffects: true`, `timeout: 5000` and an unchanged expression, followed by a successful result. Disable logging with `DebugAdapterHost.Logging /Off` afterward. This tests absence of duplicate execution, not whether the backend enforces all restriction flags.

## Navigation acceptance check

After the server finishes importing the Gradle project:

1. In Main.kt, put the caret on `message` on line 5 and press **F12**. Greeting.kt should open with `message` selected on line 4 (the name starts at column 9).
2. Press **Shift+F12** on that declaration. Find All References should show the declaration and the calls in Main.kt line 5 and Preview.kt line 3.
3. The separate `OtherGreeting.message()` and its call on Preview.kt line 10 must not appear in this result set. This distinguishes semantic references from text matches.
4. Double-click Preview.kt's result: the editor should open line 3, column 45.

Use the default Visual Studio keyboard profile, or bind Edit.GoToDefinition and Edit.FindAllReferences to F12 and Shift+F12 in Tools > Options > Environment > Keyboard. The extension respects custom keyboard bindings.

