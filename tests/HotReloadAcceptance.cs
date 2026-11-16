// Native Visual Studio acceptance for Kotlin/JVM hot reload (T72), driven through typed EnvDTE calls. Compiled by
// verify-hot-reload.ps1. A Kotlin console project from the New Project template's generator runs a loop that prints a text from
// another class; the checks edit that class in the editor and read the Kotlin and Debug Output panes, the Error List
// and the debugger state.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;

public sealed class HotReloadAcceptance
{
    private const string KotlinCommands = "{75625a1a-f6e1-42aa-bf3f-90432c756124}";
    private const int HotReloadCommand = 0x10B;
    private DTE2 dte;
    private readonly Action<string> say;
    private int passed;
    private string main = "", text = "", package = "";

    public HotReloadAcceptance(Action<string> say)
    {
        this.say = say;
    }

    /// <summary>The automation object of the Visual Studio instance that opened the project.</summary>
    public void Attach(object dte)
    {
        this.dte = (DTE2)dte;
    }

    public int Passed => passed;

    private void Check(bool condition, string message)
    {
        if (!condition) { say("FAIL: " + message); throw new InvalidOperationException(message); }
        passed++;
        say("PASS: " + message);
    }

    private static bool WaitFor(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            try { if (condition()) return true; } catch (Exception) { }
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    public string Pane(string name)
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (pane.Name != name) continue;
            var document = pane.TextDocument;
            return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        }
        return "";
    }

    private static int Count(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    /// <summary>Replaces the template's program with a loop that prints a text from a second class, before the project is built.</summary>
    public void WriteProgram(string projectFile)
    {
        var sources = Path.Combine(Path.GetDirectoryName(projectFile), "src", "main", "kotlin");
        main = Directory.GetFiles(sources, "Main.kt", SearchOption.AllDirectories).First();
        var declared = Regex.Match(File.ReadAllText(main), @"^\s*package\s+([\w.]+)", RegexOptions.Multiline);
        package = declared.Success ? "package " + declared.Groups[1].Value + "\n\n" : "";
        text = Path.Combine(Path.GetDirectoryName(main), "HotText.kt");
        // The template's unit test calls the function this program replaces, and the launch builds the whole project.
        // Emptied, not deleted: the project is loaded and shows these files.
        var tests = Path.Combine(Path.GetDirectoryName(projectFile), "src", "test");
        if (Directory.Exists(tests))
            foreach (var test in Directory.GetFiles(tests, "*.kt", SearchOption.AllDirectories)) File.WriteAllText(test, "// Emptied by the hot reload acceptance.\n");
        File.WriteAllText(main, package + "fun main() {\n    var i = 0\n    while (i < 7200) {\n        println(\"TICK \" + HotText.value())\n        Thread.sleep(250)\n        i++\n    }\n}\n");
        File.WriteAllText(text, TextSource("kotlin-1"));
        say("Program: " + main + " prints HotText.value() from " + text);
    }

    // The return statement keeps its line in every version, so a breakpoint there stays valid across reloads.
    private string TextSource(string value) => package + "object HotText {\n    fun value(): String {\n        val text = \"" + value + "\"\n        return text\n    }\n}\n";
    private int LineOf(string file, string part) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(part)) + 1;

    /// <summary>Types new content into the document in the editor, as an edit would, and optionally saves it.</summary>
    private Document Edit(string file, string content, bool save)
    {
        dte.ItemOperations.OpenFile(file, EnvDTE.Constants.vsViewKindCode);
        var document = dte.Documents.Cast<Document>().First(d => string.Equals(d.FullName, file, StringComparison.OrdinalIgnoreCase));
        var textDocument = (TextDocument)document.Object("TextDocument");
        textDocument.StartPoint.CreateEditPoint().ReplaceText(textDocument.EndPoint, content, 0);
        if (save) document.Save();
        return document;
    }

    private Command Command() => dte.Commands.Item(KotlinCommands, HotReloadCommand);

    private int ErrorsIn(string file)
    {
        var items = dte.ToolWindows.ErrorList.ErrorItems;
        var count = 0;
        for (var i = 1; i <= items.Count; i++)
            if (string.Equals(items.Item(i).FileName, file, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    private void DeleteBreakpoints() => dte.Debugger.Breakpoints.Cast<Breakpoint>().ToList().ForEach(b => b.Delete());

    private bool Break(int seconds) => WaitFor(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, seconds);

    /// <summary>
    /// Starts the program, then reloads by save, by command, with a compiler error and with a change the JVM refuses,
    /// and checks the end of the session. With <paramref name="debug"/> (F5) it also reloads at a breakpoint; without
    /// (Ctrl+F5) the program runs with no debugger attached.
    /// </summary>
    public void Run(bool debug)
    {
        const string reloaded = "Hot reload: 1 class reloaded.";
        Check(Command().Name == "Kotlin.HotReload", "The command is registered as Kotlin.HotReload: " + Command().Name);
        var started = DateTime.Now;
        // The first launch loads the Kotlin server, imports the Gradle project and builds it.
        if (debug)
        {
            var line = LineOf(main, "println(");
            dte.Debugger.Breakpoints.Add("", main, line);
            dte.ExecuteCommand("Debug.Start");
            // The session can end before the program starts (the Kotlin server's debugger waits 30 s for the JVM): stop
            // waiting once the debugger is back in design mode.
            var left = false;
            WaitFor(() =>
            {
                var mode = dte.Debugger.CurrentMode;
                if (mode != dbgDebugMode.dbgDesignMode) left = true;
                return mode == dbgDebugMode.dbgBreakMode || (left && mode == dbgDebugMode.dbgDesignMode);
            }, 1200);
            var stopped = dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            if (!stopped) { say("--- Kotlin pane:\n" + Pane("Kotlin")); say("--- Debug pane:\n" + Pane("Debug")); }
            Check(stopped, "F5 stopped at the breakpoint in Main.kt:" + line + " (" + (int)(DateTime.Now - started).TotalSeconds + " s after F5)");
            DeleteBreakpoints();
            dte.Debugger.Go(false);
        }
        else dte.ExecuteCommand("Debug.StartWithoutDebugging");
        var printed = WaitFor(() => Pane("Debug").Contains("TICK kotlin-1"), debug ? 60 : 1200);
        if (!printed) { say("--- Kotlin pane:\n" + Pane("Kotlin")); say("--- Debug pane:\n" + Pane("Debug")); }
        Check(printed, "The program prints TICK kotlin-1 in the Debug pane (" + (int)(DateTime.Now - started).TotalSeconds + " s after " + (debug ? "F5" : "Ctrl+F5") + ")");
        Check(WaitFor(() => Command().IsAvailable, 30), "Kotlin.HotReload is available while the program runs");

        // Reload on save.
        started = DateTime.Now;
        Edit(text, TextSource("kotlin-2"), save: true);
        var done = WaitFor(() => Count(Pane("Kotlin"), reloaded) == 1 || Pane("Kotlin").Contains("Hot reload failed"), 900);
        if (!done || !Pane("Kotlin").Contains(reloaded)) say("--- Kotlin pane:\n" + Pane("Kotlin"));
        Check(Count(Pane("Kotlin"), reloaded) == 1, "Saving HotText.kt reloads its class (" + (int)(DateTime.Now - started).TotalSeconds + " s, the first reload starts the Gradle daemon)");
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-2"), 30), "The running program prints TICK kotlin-2");

        // Reload by command, from an unsaved edit.
        started = DateTime.Now;
        var document = Edit(text, TextSource("kotlin-3"), save: false);
        Check(!document.Saved, "The edit is not saved before the command");
        dte.ExecuteCommand("Kotlin.HotReload");
        done = WaitFor(() => Count(Pane("Kotlin"), reloaded) == 2 || Count(Pane("Kotlin"), "Hot reload failed") > 0, 300);
        if (Count(Pane("Kotlin"), reloaded) != 2) say("--- Kotlin pane:\n" + Pane("Kotlin"));
        Check(Count(Pane("Kotlin"), reloaded) == 2, "Kotlin.HotReload reloads the class (" + (int)(DateTime.Now - started).TotalSeconds + " s)");
        Check(document.Saved, "The command saved the modified document");
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-3"), 30), "The running program prints TICK kotlin-3");
        System.Threading.Thread.Sleep(3000);
        Check(Count(Pane("Kotlin"), "Hot reload:") == 2, "The command's own save did not start a second reload");

        // A compiler error: nothing is reloaded, the error is in the Error List.
        Edit(text, TextSource("kotlin-4").Replace("return text", "return missing"), save: true);
        Check(WaitFor(() => Pane("Kotlin").Contains("Hot reload failed: Compilation failed: HotText.kt"), 300), "A compiler error is reported as a failed reload in the Kotlin pane");
        Check(WaitFor(() => ErrorsIn(text) > 0, 30), "The compiler error is in the Error List for HotText.kt");
        Check(!Pane("Debug").Contains("kotlin-4"), "The program still runs the previous code");
        Edit(text, TextSource("kotlin-4"), save: true);
        Check(WaitFor(() => Count(Pane("Kotlin"), reloaded) == 3, 300), "The fixed class reloads");
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-4"), 30), "The running program prints TICK kotlin-4");
        Check(WaitFor(() => ErrorsIn(text) == 0, 30), "The Error List entry is gone after the successful reload");

        // A structural change is refused by the JVM.
        Edit(text, TextSource("kotlin-5").Replace("return text", "return text + extra()").Replace("\n}\n", "\n    private fun extra(): String = \"!\"\n}\n"), save: true);
        Check(WaitFor(() => Pane("Kotlin").Contains("Hot reload: 1 class could not be reloaded.") && Pane("Kotlin").Contains("attempted to add a method"), 300),
            "An added method is reported as refused, with the JVM's reason");

        Edit(text, TextSource("kotlin-6"), save: true);
        Check(WaitFor(() => Count(Pane("Kotlin"), reloaded) == 4, 300), "The class reloads again once its shape matches");
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-6"), 30), "The running program prints TICK kotlin-6");
        if (debug) BreakpointChecks(reloaded);
        Stop();
    }

    /// <summary>Paused at a breakpoint: the reload waits and is applied when the program continues.</summary>
    private void BreakpointChecks(string reloaded)
    {
        dte.Debugger.Breakpoints.Add("", text, LineOf(text, "return text"));
        Check(Break(60), "A breakpoint added after reloads stops in HotText.kt");
        var value = dte.Debugger.CurrentStackFrame.Locals.Cast<Expression>().FirstOrDefault(l => l.Name == "text");
        Check(value != null && value.Value.Contains("kotlin-6"), "The stopped frame runs the reloaded code: text = " + (value == null ? "(no local)" : value.Value));
        Edit(text, TextSource("kotlin-7"), save: true);
        Check(WaitFor(() => Pane("Kotlin").Contains("A program paused in the debugger takes them when it continues."), 300), "A reload at a breakpoint announces that it waits");
        Check(Count(Pane("Kotlin"), reloaded) == 4, "Nothing is reloaded while the program is paused");
        DeleteBreakpoints();
        dte.Debugger.Go(false);
        Check(WaitFor(() => Count(Pane("Kotlin"), reloaded) == 5, 120), "The reload is applied when the program continues");
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-7"), 30), "The running program prints TICK kotlin-7");

        // A breakpoint that was set before a reload of its class.
        dte.Debugger.Breakpoints.Add("", text, LineOf(text, "return text"));
        Check(Break(60), "The breakpoint stops again");
        dte.Debugger.Go(false);
        Check(Break(60), "The breakpoint stops on the next loop turn");
        Edit(text, TextSource("kotlin-8"), save: true);
        dte.Debugger.Go(false);
        Check(WaitFor(() => Count(Pane("Kotlin"), reloaded) == 6, 300), "The class reloads while its breakpoint is set");
        var again = Break(30);
        say("INFO: a breakpoint set before the reload of its class stops afterwards: " + again);
        if (again)
        {
            value = dte.Debugger.CurrentStackFrame.Locals.Cast<Expression>().FirstOrDefault(l => l.Name == "text");
            say("INFO: stopped with text = " + (value == null ? "(no local)" : value.Value));
        }
        DeleteBreakpoints();
        if (again) dte.Debugger.Go(false);
        Check(WaitFor(() => Pane("Debug").Contains("TICK kotlin-8"), 30), "The running program prints TICK kotlin-8");

    }

    /// <summary>The end of the session ends hot reload and its Gradle daemon.</summary>
    private void Stop()
    {
        dte.Debugger.Stop(true);
        Check(WaitFor(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 60), "Stop Debugging ends the session");
        Check(WaitFor(() => !Command().IsAvailable, 30), "Kotlin.HotReload is unavailable without a running program");
        Check(WaitFor(() => SessionDaemons() == 0, 30), "The session's Gradle daemons (launch and hot reload) ended with it");
        say("--- Kotlin pane (launch and hot reload lines):\n" + string.Join("\n", Pane("Kotlin").Split('\n')
            .Where(l => l.Contains("Hot reload") || l.Contains("Gradle daemon") || l.Contains("Kotlin debugging") || l.StartsWith("  ")).Select(l => l.TrimEnd())));
    }

    /// <summary>
    /// Gradle daemons of Vistudio's private registries that still run: the debug session's launch daemon under
    /// %LOCALAPPDATA%\Vistudio\debug-daemon and the hot reload daemon under %LOCALAPPDATA%\Vistudio\hot-reload. Each
    /// daemon writes daemon-&lt;pid&gt;.out.log under its registry's &lt;gradle version&gt; folder and keeps it open,
    /// so a registry folder that could be removed has no daemon.
    /// </summary>
    private static int SessionDaemons()
    {
        return Daemons("hot-reload") + Daemons("debug-daemon");
    }

    private static int Daemons(string area)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", area);
        if (!Directory.Exists(folder)) return 0;
        var running = 0;
        foreach (var log in Directory.GetFiles(folder, "daemon-*.out.log", SearchOption.AllDirectories))
        {
            int pid;
            if (!int.TryParse(Path.GetFileName(log).Split('-', '.')[1], out pid)) continue;
            try { using (var process = System.Diagnostics.Process.GetProcessById(pid)) if (process.ProcessName == "java" && process.StartTime <= File.GetLastWriteTime(log).AddMinutes(1)) running++; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return running;
    }
}
