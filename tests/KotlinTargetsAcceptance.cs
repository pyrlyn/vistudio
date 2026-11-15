// Compiled by tests/verify-kotlin-targets-native.ps1 against the IDE's Microsoft.VisualStudio.Interop.dll.
// Native Visual Studio acceptance of Kotlin Multiplatform targets (T21) on a copy of samples/kotlin-multiplatform.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Xml.Linq;
using Thread = System.Threading.Thread;
using EnvDTE;
using EnvDTE80;

public sealed class KotlinTargetsAcceptance
{
    private const string Commands = "{75625a1a-f6e1-42aa-bf3f-90432c756124}";
    private readonly DTE2 dte;
    private readonly int processId;
    // Read before any modal dialog: Visual Studio rejects automation calls while a message box is open.
    private IntPtr mainWindowHandle;
    private readonly string workspace, projectFile, evidence;
    private readonly Action<string> say;
    private int passed;

    /// <summary>Skips the JVM Ctrl+F5 run, which needs the configured Kotlin language server.</summary>
    public bool SkipJvmRun { get; set; }

    public KotlinTargetsAcceptance(object dte, int processId, string workspace, string projectFile, Action<string> say)
    {
        this.dte = (DTE2)dte;
        this.processId = processId;
        this.workspace = workspace;
        this.projectFile = projectFile;
        evidence = Path.GetDirectoryName(workspace)!;
        this.say = say;
    }

    private void Check(bool condition, string message)
    {
        if (!condition) { say("FAIL: " + message); throw new Exception(message); }
        passed++;
        say("PASS: " + message);
    }

    private static void Wait(Func<bool> condition, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds && !condition(); waited += 250) Thread.Sleep(250);
    }

    private UIHierarchy Explorer => dte.ToolWindows.SolutionExplorer;

    private UIHierarchyItem ProjectNode()
    {
        UIHierarchyItem result = null;
        Wait(() =>
        {
            var solution = Explorer.UIHierarchyItems.Item(1);
            solution.UIHierarchyItems.Expanded = true;
            result = solution.UIHierarchyItems.Cast<UIHierarchyItem>().FirstOrDefault(i => !i.Name.Contains("(unloaded)") && !i.Name.Contains("load failed"));
            return result != null;
        }, 60000);
        return result!;
    }

    private static UIHierarchyItem Find(UIHierarchyItem node, params string[] names)
    {
        foreach (var name in names)
        {
            node.UIHierarchyItems.Expanded = true;
            node = node.UIHierarchyItems.Cast<UIHierarchyItem>().FirstOrDefault(c => c.Name == name)!;
            if (node == null) return null;
        }
        return node;
    }

    private string[] Snapshot(string file, int maxDepth)
    {
        var lines = new List<string>();
        void Dump(UIHierarchyItem node, int depth)
        {
            lines.Add(new string(' ', depth * 2) + node.Name);
            if (depth >= maxDepth || node.Name == "Gradle Scripts" || node.Name == "Toolchain" || Regex.IsMatch(node.Name, @"\(\d+\)$")) return;
            node.UIHierarchyItems.Expanded = true;
            foreach (UIHierarchyItem child in node.UIHierarchyItems) Dump(child, depth + 1);
        }
        Dump(ProjectNode(), 0);
        File.WriteAllLines(Path.Combine(evidence, file), lines);
        return lines.ToArray();
    }

    private string PaneText(string name)
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (pane.Name != name) continue;
            var document = pane.TextDocument;
            return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        }
        return "";
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    private void WaitForBuild()
    {
        Wait(() => dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateDone, 600000);
        Thread.Sleep(500);
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    private struct Rect { public int Left, Top, Right, Bottom; }

    private void Screenshot(string file) => CaptureWindow((IntPtr)dte.MainWindow.HWnd, Path.Combine(evidence, file));

    /// <summary>Saves the screen area of a window; needs no DTE, so worker threads can call it.</summary>
    private static void CaptureWindow(IntPtr handle, string path)
    {
        SetProcessDPIAware();
        SetForegroundWindow(handle);
        Thread.Sleep(1000);
        GetWindowRect(handle, out var rect);
        using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    private void SetStartupTarget(string value)
    {
        // Same effect as Properties > Startup target or Edit Project File; the project's watcher reloads the metadata.
        var document = XDocument.Load(projectFile);
        document.Root!.Elements("PropertyGroup").First(e => (string?)e.Attribute("Label") == "Vistudio").SetElementValue("StartupTarget", value);
        document.Save(projectFile);
        Thread.Sleep(2500);
    }

    /// <summary>Runs a target context command on the target node and waits for its Gradle invocation to finish.</summary>
    private string RunTargetCommand(string target, string commandName, string expectedTasks)
    {
        var node = Find(ProjectNode(), "vistudio-multiplatform", "Targets", target);
        Check(node != null, "Solution Explorer shows target node '" + target + "'");
        Explorer.Parent.Activate();
        node!.Select(vsUISelectionType.vsUISelectionTypeSelect);
        Thread.Sleep(500);
        var selected = string.Join(" | ", ((object[])Explorer.SelectedItems).Cast<UIHierarchyItem>().Select(i => i.Name));
        var command = dte.Commands.Item(commandName);
        say("Selected: " + selected + "; " + commandName + " available: " + command.IsAvailable);
        var before = PaneText("Kotlin");
        var finished = Count(before, "Gradle tasks succeeded.") + Count(before, "Gradle tasks FAILED");
        dte.ExecuteCommand(commandName);
        Wait(() => { var t = PaneText("Kotlin"); return Count(t, "Gradle tasks succeeded.") + Count(t, "Gradle tasks FAILED") > finished; }, 600000);
        var text = PaneText("Kotlin").Substring(before.Length);
        File.WriteAllText(Path.Combine(evidence, commandName.Replace('.', '-') + "-" + target.Split(' ')[0] + ".txt"), text);
        Check(text.Contains("Gradle " + expectedTasks + " in ") && text.Contains("Gradle tasks succeeded."),
            commandName + " on " + target + " ran Gradle " + expectedTasks + " successfully");
        return text;
    }

    /// <summary>Runs <paramref name="trigger"/> and clicks OK on the Visual Studio message box that contains <paramref name="fragment"/>, returning its text. The watcher starts first, because the command may not return while the box is open.</summary>
    private string CloseMessageBox(string fragment, Action trigger)
    {
        // UI Automation runs on an MTA thread: from this STA thread, calls into the modal message box did not return
        // (Visual Studio 18.12). The worker touches neither DTE nor the logging callback, which belong to this thread.
        var mainWindow = mainWindowHandle;
        string? text = null, buttonsSeen = null, screenshot = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Wait(() =>
                {
                    // Only dialogs: the main window's own tree is large and never holds the message box.
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
                    foreach (AutomationElement window in windows)
                    {
                        var candidates = window.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>()
                            .Concat((IntPtr)window.Current.NativeWindowHandle == mainWindow ? Enumerable.Empty<AutomationElement>() : new[] { window });
                        foreach (var dialog in candidates)
                        {
                            var texts = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                                .Select(e => e.Current.Name).ToArray();
                            var joined = string.Join("\n", texts);
                            if (!joined.Contains(fragment)) continue;
                            text = joined;
                            screenshot = Path.Combine(evidence, "f5-message.png");
                            CaptureWindow(mainWindow, screenshot);
                            var buttons = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                                .Cast<AutomationElement>().ToArray();
                            buttonsSeen = string.Join(", ", buttons.Select(b => "'" + b.Current.Name + "'"));
                            var ok = buttons.FirstOrDefault(b => b.Current.Name.Replace("_", "").Replace("&", "") == "OK");
                            if (ok != null && ok.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
                            else if (dialog.TryGetCurrentPattern(WindowPattern.Pattern, out var closable)) ((WindowPattern)closable).Close();
                            return true;
                        }
                    }
                    return false;
                }, 60000);
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        worker.SetApartmentState(System.Threading.ApartmentState.MTA);
        worker.Start();
        trigger();
        Check(worker.Join(90000), "The message box automation finished within 90 s");
        if (buttonsSeen != null) say("Message box buttons: " + buttonsSeen);
        if (failure != null) say("Message box automation failed: " + failure.Message);
        return text ?? "";
    }

    public int Run()
    {
        mainWindowHandle = (IntPtr)dte.MainWindow.HWnd;
        dte.MainWindow.Activate();
        dte.ExecuteCommand("View.SolutionExplorer");
        var project = ProjectNode();
        Check(project != null, "Solution Explorer loaded the Multiplatform .kotlinproj as project '" + project?.Name + "'");

        project!.Select(vsUISelectionType.vsUISelectionTypeSelect);
        var import = dte.Commands.Item(Commands, 0x200);
        bool Imported() { try { return File.ReadAllText(projectFile).Contains("<Target Include="); } catch (IOException) { return false; } }
        dte.ExecuteCommand(import.Name);
        Wait(Imported, 600000);
        Check(Imported(), "In-IDE Import Gradle Model recorded Kotlin targets in the .kotlinproj");
        var metadata = File.ReadAllText(projectFile);
        File.Copy(projectFile, Path.Combine(evidence, "vistudio.kotlinproj"), true);
        Check(metadata.Contains("<ProjectKind>Multiplatform</ProjectKind>") && metadata.Contains("<Targets>:jvm :js :wasmJs :mingwX64</Targets>"),
            "The project file describes a Multiplatform project with jvm, js, wasmJs and mingwX64");
        Thread.Sleep(3000);

        var tree = Snapshot("tree-targets.txt", 6).Select(l => l.Trim()).ToArray();
        foreach (var expected in new[] { "commonMain", "webMain", "Targets", "jvm (JVM)", "js (JS)", "wasmJs (Wasm)", "mingwX64 (Native)",
            "jvmMain", "jsMain", "wasmJsMain", "mingwX64Main", "src/jvmMain/kotlin", "Platform.jvm.kt", "Platform.mingwX64.kt", "Web.kt", "Dependencies" })
            Check(tree.Contains(expected), "Solution Explorer tree contains '" + expected + "'");
        Check(tree.Any(l => Regex.IsMatch(l, @"^jvmRuntimeClasspath \(\d+\)$")) && tree.Any(l => Regex.IsMatch(l, @"^mingwX64CompileKlibraries \(\d+\)$"))
            && tree.Any(l => Regex.IsMatch(l, @"^wasmJsCompileClasspath \(\d+\)$")), "Each target lists its own dependency configurations");
        var module = Find(ProjectNode(), "vistudio-multiplatform") ?? ProjectNode();
        foreach (UIHierarchyItem child in module.UIHierarchyItems) if (child.Name == "Dependencies" || child.Name == "Frameworks") child.UIHierarchyItems.Expanded = false;
        var targets = Find(module, "Targets");
        foreach (UIHierarchyItem target in targets!.UIHierarchyItems)
        {
            var dependencies = Find(target, "Dependencies");
            if (dependencies != null) dependencies.UIHierarchyItems.Expanded = false;
        }
        Find(targets, "jvm (JVM)", "jvmMain", "src/jvmMain/kotlin", "Platform.jvm.kt")?.Select(vsUISelectionType.vsUISelectionTypeSelect);
        Screenshot("solution-explorer-targets.png");

        // Target context commands run the target's imported Gradle tasks.
        var run = RunTargetCommand("js (JS)", "Kotlin.Target.Run", ":jsNodeDevelopmentRun");
        Check(run.Contains("Platform: Web/JS") && run.Contains("VISTUDIO_KMP_PASS"), "Run js executes the JS implementation on Node.js");
        RunTargetCommand("mingwX64 (Native)", "Kotlin.Target.Build", ":mingwX64MainKlibrary :linkDebugExecutableMingwX64");
        Check(File.Exists(Path.Combine(workspace, "build", "bin", "mingwX64", "debugExecutable", "vistudio-multiplatform.exe")), "Build mingwX64 linked the native debug executable");
        run = RunTargetCommand("mingwX64 (Native)", "Kotlin.Target.Run", ":runDebugExecutableMingwX64");
        Check(run.Contains("Platform: Native mingwX64") && run.Contains("VISTUDIO_KMP_PASS"), "Run mingwX64 executes the native binary");
        run = RunTargetCommand("wasmJs (Wasm)", "Kotlin.Target.Run", ":wasmJsNodeDevelopmentRun");
        Check(run.Contains("Platform: Web/Wasm") && run.Contains("VISTUDIO_KMP_PASS"), "Run wasmJs executes the Wasm implementation on Node.js");
        RunTargetCommand("jvm (JVM)", "Kotlin.Target.Test", ":jvmTest");

        // Solution Build stays on Gradle and builds all targets.
        var build = dte.Solution.SolutionBuild;
        build.Build(true); WaitForBuild();
        var document = dte.ToolWindows.OutputWindow.OutputWindowPanes.Item("Build").TextDocument;
        var pane = document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        File.WriteAllText(Path.Combine(evidence, "build-output.txt"), pane);
        Check(build.LastBuildInfo == 0 && pane.Contains("BUILD SUCCESSFUL") && pane.Contains("Gradle build in"), "Solution Build runs Gradle build for all targets and succeeds");

        // F5 on a JS startup target explains that only JVM debugging exists; Ctrl+F5 runs the target through Gradle.
        SetStartupTarget("js");
        Check(XDocument.Load(projectFile).Root!.Elements("PropertyGroup").First(e => (string?)e.Attribute("Label") == "Vistudio").Element("StartupTarget")!.Value == "js",
            "StartupTarget js is written to the project file");
        var message = CloseMessageBox("Debugging is not supported", () => dte.ExecuteCommand("Debug.Start"));
        File.WriteAllText(Path.Combine(evidence, "f5-message.txt"), message);
        Check(message.Contains("JS, Node.js") && message.Contains(":js") && message.Contains("Ctrl+F5") && message.Contains(":jsNodeDevelopmentRun"),
            "F5 on the JS startup target shows the 'debugging not supported' message with the Gradle run task");
        Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 10000);
        Check(dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode && !PaneText("Kotlin").Contains("Kotlin debug target:"), "No debug session or JVM launch was started for JS");

        var before = PaneText("Kotlin");
        var platforms = Count(before, "Platform: Web/JS");
        dte.ExecuteCommand("Debug.StartWithoutDebugging");
        Wait(() => Count(PaneText("Kotlin"), "Platform: Web/JS") > platforms && PaneText("Kotlin").Substring(before.Length).Contains("Gradle tasks succeeded."), 600000);
        var ctrlF5 = PaneText("Kotlin").Substring(before.Length);
        File.WriteAllText(Path.Combine(evidence, "ctrl-f5-js.txt"), ctrlF5);
        Check(ctrlF5.Contains("Kotlin run target: :js (JS, Node.js)") && ctrlF5.Contains("Gradle :jsNodeDevelopmentRun in ") && ctrlF5.Contains("Platform: Web/JS"),
            "Ctrl+F5 on the JS startup target runs :jsNodeDevelopmentRun through Gradle");

        if (SkipJvmRun) say("SKIPPED: JVM Ctrl+F5 (language server unavailable; -SkipJvmRun)");
        else
        {
            SetStartupTarget(":jvm");
            foreach (Document open in dte.Documents) open.Close(vsSaveChanges.vsSaveChangesNo);
            before = PaneText("Kotlin");
            dte.ExecuteCommand("Debug.StartWithoutDebugging");
            Wait(() => PaneText("Kotlin").Substring(before.Length).Contains("Kotlin run target: sample.MainKt"), 300000);
            Wait(() => dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode, 300000);
            Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 300000);
            var debugPane = PaneText("Debug");
            File.WriteAllText(Path.Combine(evidence, "ctrl-f5-jvm.txt"), PaneText("Kotlin").Substring(before.Length) + Environment.NewLine + "--- Debug pane ---" + Environment.NewLine + debugPane);
            Check(PaneText("Kotlin").Substring(before.Length).Contains("Kotlin run target: sample.MainKt") && debugPane.Contains("Platform: JVM"),
                "Ctrl+F5 on the JVM startup target launches sample.MainKt from commonMain through the JVM adapter");
        }
        SetStartupTarget("");
        return passed;
    }
}
