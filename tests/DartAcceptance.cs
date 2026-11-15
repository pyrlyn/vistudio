// Native Visual Studio acceptance for the Dart and Flutter integration, driven through typed EnvDTE calls
// (PowerShell's late-bound COM adapter cannot reach DTE2 tool windows in VS 18). Compiled by verify-dart.ps1.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EnvDTE;
using EnvDTE80;

public sealed class DartAcceptance
{
    private readonly DTE2 dte;
    private readonly Action<string> say;
    private int passed;

    public DartAcceptance(object dte, Action<string> say)
    {
        this.dte = (DTE2)dte;
        this.say = say;
    }

    private void Check(bool condition, string message)
    {
        if (!condition) { say("FAIL: " + message); throw new Exception(message); }
        passed++;
        say("PASS: " + message);
    }

    private static bool Wait(Func<bool> condition, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 500)
        {
            try { if (condition()) return true; }
            catch (System.Runtime.InteropServices.COMException) { }
            System.Threading.Thread.Sleep(500);
        }
        try { return condition(); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private string Pane(string name)
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (pane.Name != name) continue;
            var document = pane.TextDocument;
            return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        }
        return "";
    }

    private string DartPane => Pane("Dart");
    private string DebugPane => Pane("Debug");

    private void ClearDartPane()
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
            if (pane.Name == "Dart") pane.Clear();
    }

    private string[] Errors()
    {
        var items = dte.ToolWindows.ErrorList.ErrorItems;
        var result = new string[items.Count];
        for (var i = 1; i <= items.Count; i++)
        {
            var item = items.Item(i);
            result[i - 1] = item.ErrorLevel + "|" + Path.GetFileName(item.FileName) + "|" + item.Line + "|" + item.Description;
        }
        return result;
    }

    private Document Open(string path)
    {
        dte.ItemOperations.OpenFile(path);
        Wait(() => dte.ActiveDocument != null && string.Equals(dte.ActiveDocument.FullName, path, StringComparison.OrdinalIgnoreCase), 30000);
        return dte.ActiveDocument;
    }

    private static TextDocument Text(Document document) => (TextDocument)document.Object("TextDocument");

    private static void Replace(Document document, string oldText, string newText)
    {
        var text = Text(document);
        var all = text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
        if (!all.Contains(oldText)) throw new Exception("Text not found in " + document.Name + ": " + oldText);
        var start = text.StartPoint.CreateEditPoint();
        start.ReplacePattern(text.EndPoint, oldText, newText, (int)vsFindOptions.vsFindOptionsMatchCase);
    }

    private void Run(string command, string arguments = "")
    {
        say("> " + command);
        dte.ExecuteCommand(command, arguments);
    }

    /// <summary>Dart console package: analysis server, Error List, run, tests, analyze, debugging.</summary>
    public int RunConsole(string root)
    {
        var program = Path.Combine(root, "bin", "dart_console.dart");
        var test = Path.Combine(root, "test", "dart_console_test.dart");
        var document = Open(program);
        Check(document != null, "bin\\dart_console.dart opened in the code editor (language " + document?.Language + ")");
        Check(Wait(() => DartPane.Contains("Dart analysis server initialized."), 120000), "The Dart analysis server started from the IDE: " + Last(DartPane));
        Check(DartPane.Contains("Dart 3.14.0") && DartPane.Contains("Flutter 3.50.0"), "SDK versions logged from the workspace's .vs\\settings.json");

        // Error reporting: an unsaved error reaches the Error List through the analysis server, and clears again.
        Replace(document!, "print('Total area: $total');", "print('Total area: $total');\n  print(missingThing);");
        Check(Wait(() => Errors().Any(e => e.Contains("missingThing") && e.Contains("dart_console.dart") && e.Contains("|10|")), 60000),
            "Analysis server error in the Error List at line 10: " + string.Join(" / ", Errors()));
        Replace(document!, "\n  print(missingThing);", "");
        Check(Wait(() => !Errors().Any(e => e.Contains("missingThing")), 60000), "The error disappears after the fix");
        document!.Save();

        ClearDartPane();
        Run("Dart.Run");
        Check(Wait(() => DartPane.Contains("exited with code 0"), 120000) && DartPane.Contains("Total area: 9.14"), "Dart: Run (dart run) output: " + Last(DartPane));

        Run("Dart.RunTests");
        Check(Wait(() => DartPane.Contains("3 passed, 0 failed"), 180000), "Dart: Run Unit Tests: " + Last(DartPane));

        var failing = Path.Combine(root, "test", "failing_test.dart");
        File.WriteAllText(failing, "import 'package:test/test.dart';\n\nvoid main() {\n  test('wrong sum', () {\n    expect(1 + 1, 3);\n  });\n}\n");
        Open(failing);
        Run("Dart.RunTestFile");
        Check(Wait(() => DartPane.Contains("0 passed, 1 failed"), 180000), "Dart: Run Tests in File on a failing test: " + Last(DartPane));
        Check(Wait(() => Errors().Any(e => e.Contains("Test failed: wrong sum") && e.Contains("failing_test.dart|4|")), 20000), "Failed test in the Error List at its declaration: " + string.Join(" / ", Errors()));
        dte.ActiveDocument.Close(vsSaveChanges.vsSaveChangesNo);
        File.Delete(failing);

        var extra = Path.Combine(root, "lib", "extra.dart");
        File.WriteAllText(extra, "int broken() => undefinedThing;\n");
        // Only output written after each run counts: the pane keeps earlier summaries.
        var mark = DartPane.Length;
        Run("Dart.Analyze");
        Check(Wait(() => After(DartPane, mark).Contains("dart analyze: 1 errors"), 120000), "Dart: Analyze Package: " + Last(After(DartPane, mark)));
        Check(Wait(() => Errors().Any(e => e.Contains("undefinedThing") && e.Contains("extra.dart")), 30000), "dart analyze error in the Error List");
        File.Delete(extra);
        mark = DartPane.Length;
        Run("Dart.Analyze");
        Check(Wait(() => After(DartPane, mark).Contains("dart analyze: 0 errors"), 120000), "Analyze again after the fix: " + Last(After(DartPane, mark)));
        Check(Wait(() => !Errors().Any(e => e.Contains("undefinedThing")), 30000), "The dart analyze error left the Error List");

        // Debugging through the SDK's debug adapter: breakpoint, call stack, watch expression, continue.
        Open(program);
        var line = File.ReadAllLines(program).ToList().FindIndex(l => l.Contains("final total = totalArea(shapes);")) + 1;
        dte.Debugger.Breakpoints.Add("", program, line);
        Run("Dart.Debug");
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode && dte.Debugger.CurrentStackFrame != null, 120000), "Dart: Debug stopped at the breakpoint (break mode)");
        var frame = dte.Debugger.CurrentStackFrame;
        Check(frame.FunctionName.Contains("main"), "Current frame: " + frame.FunctionName + " (" + frame.Language + ")");
        var value = dte.Debugger.GetExpression("shapes.length + 40", true, 10000);
        Check(value.IsValidValue && value.Value == "42", "Watch expression shapes.length + 40 = " + value.Value);
        var locals = frame.Locals.Cast<Expression>().Select(e => e.Name + "=" + e.Value).ToList();
        Check(locals.Any(l => l.StartsWith("shapes=")), "Locals window: " + string.Join(", ", locals));
        dte.Debugger.Breakpoints.Item(1).Delete();
        dte.Debugger.Go(false);
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 60000), "The debug session ended after Continue");
        Check(DebugPane.Contains("Total area: 9.14"), "Program output in Debug Output");

        // Debug one test (what the CodeLens "Debug test" button runs) through the test debug adapter.
        Open(test);
        var testLine = File.ReadAllLines(test).ToList().FindIndex(l => l.Contains("expect(add(2, 3), 5);")) + 1;
        dte.Debugger.Breakpoints.Add("", test, testLine);
        Run("Dart.DebugTestFile");
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, 180000), "Dart: Debug Tests in File stopped in the test");
        Check(dte.Debugger.CurrentStackFrame != null && File.ReadAllLines(test).Length > 0, "Test frame: " + dte.Debugger.CurrentStackFrame?.FunctionName);
        dte.Debugger.Breakpoints.Item(1).Delete();
        dte.Debugger.Go(false);
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 120000), "The test debug session ended");
        return passed;
    }

    /// <summary>Flutter app: run with hot reload on save, hot restart, stop, debugging with hot reload, widget and integration tests.</summary>
    public int RunFlutter(string root)
    {
        var main = Path.Combine(root, "lib", "main.dart");
        var document = Open(main);
        Check(Wait(() => DartPane.Contains("Dart analysis server initialized."), 120000), "Analysis server running for the Flutter package");

        ClearDartPane();
        Run("Dart.Run");
        Check(Wait(() => DartPane.Contains("Flutter app started."), 900000), "Dart: Run started the Flutter app on Windows: " + Last(DartPane));
        Check(Wait(() => DartPane.Contains("build title=Flutter Demo Home Page"), 30000), "The app's output reaches the Dart pane");
        Replace(document!, "Flutter Demo Home Page", "Reloaded On Save");
        document!.Save();
        Check(Wait(() => DartPane.Contains("Hot reload done") && DartPane.Contains("build title=Reloaded On Save"), 60000), "Saving lib\\main.dart hot reloaded the running app: " + Last(DartPane));
        Run("Dart.HotRestart");
        Check(Wait(() => DartPane.Contains("Hot restart done"), 60000), "Flutter: Hot Restart");
        Run("Dart.HotReload");
        Check(Wait(() => DartPane.Split('\n').Count(l => l.Contains("Hot reload done")) >= 2, 60000), "Flutter: Hot Reload command");
        Run("Dart.Stop");
        Check(Wait(() => DartPane.Contains("flutter run exited"), 60000), "Dart: Stop App ended flutter run: " + Last(DartPane));
        Check(Wait(() => System.Diagnostics.Process.GetProcessesByName("flutter_counter").Length == 0, 30000), "No flutter_counter.exe is left after Stop App");

        // Flutter debugging, then a hot reload inside the debug session.
        var line = File.ReadAllLines(main).ToList().FindIndex(l => l.Contains("print('build title=")) + 1;
        dte.Debugger.Breakpoints.Add("", main, line);
        Run("Dart.Debug");
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode && dte.Debugger.CurrentStackFrame != null, 900000), "Flutter debugging stopped in build()");
        Check(dte.Debugger.CurrentStackFrame.FunctionName.Contains("build"), "Frame: " + dte.Debugger.CurrentStackFrame.FunctionName);
        var title = dte.Debugger.GetExpression("widget.title", true, 10000);
        Check(title.Value.Contains("Reloaded On Save"), "widget.title in the Watch: " + title.Value);
        dte.Debugger.Breakpoints.Item(1).Delete();
        dte.Debugger.Go(false);
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgRunMode, 30000), "Running again");
        Check(Wait(() => DebugPane.Contains("build title=Reloaded On Save"), 30000), "App output in Debug Output");
        System.Threading.Thread.Sleep(5000); // the adapter reports flutter.appStarted shortly after the first frame
        Replace(document!, "Reloaded On Save", "Reloaded While Debugging");
        document!.Save();
        Check(Wait(() => DartPane.Contains("Hot reload (debug session) done") && DebugPane.Contains("build title=Reloaded While Debugging"), 60000),
            "Saving during debugging hot reloaded the app: " + Last(DartPane));
        dte.Debugger.Stop(false);
        Check(Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 60000), "Stop Debugging ended the Flutter session");
        Check(Wait(() => System.Diagnostics.Process.GetProcessesByName("flutter_counter").Length == 0, 30000), "No flutter_counter.exe is left after Stop Debugging");

        ClearDartPane();
        Run("Dart.RunTests");
        Check(Wait(() => DartPane.Contains("1 passed, 0 failed"), 300000), "Widget test (flutter test): " + Last(DartPane));
        Run("Dart.RunIntegrationTests");
        // Each test run clears the Dart pane first.
        Check(Wait(() => DartPane.Contains("PASS  counter app tapping the button increments the counter") && DartPane.Contains("1 passed, 0 failed"), 900000),
            "Integration test on Windows: " + Last(DartPane));
        Check(Wait(() => System.Diagnostics.Process.GetProcessesByName("flutter_counter").Length == 0, 30000), "No flutter_counter.exe is left after the integration test");
        return passed;
    }

    /// <summary>Text appended after <paramref name="mark"/>; the whole text when the pane was cleared in between.</summary>
    private static string After(string text, int mark) => text.Length >= mark ? text.Substring(mark) : text;

    private static string Last(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 6)));
    }
}
