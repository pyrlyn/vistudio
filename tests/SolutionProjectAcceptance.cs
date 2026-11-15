// Compiled by tests/verify-solution-project.ps1 against the IDE's Microsoft.VisualStudio.Interop.dll.
// Typed EnvDTE calls are required: PowerShell's late-bound COM adapter cannot reach DTE2 tool windows in VS 18.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Thread = System.Threading.Thread;
using EnvDTE;
using EnvDTE80;

public sealed class SolutionProjectAcceptance
{
    private readonly DTE2 dte;
    private readonly string workspace, projectFile, evidence;
    private readonly Action<string> say;
    private int passed;

    /// <summary>Optional copy of the same Gradle project without metadata, used for the Open Folder "Create or Open .kotlinproj" flow.</summary>
    public string? FolderWorkspace { get; set; }

    /// <summary>Skips F5/Ctrl+F5 when the Kotlin language server cannot run (debug launch depends on it).</summary>
    public bool SkipDebug { get; set; }

    /// <summary>Skips the Tools &gt; Options Show hidden files step (it edits the shared settings file).</summary>
    public bool SkipHiddenFilesOption { get; set; }

    /// <summary>Optional language server launcher that exits at startup (for example an expired build) for the F5 fail-fast check.</summary>
    public string? FailingServerPath { get; set; }

    public SolutionProjectAcceptance(object dte, string workspace, string projectFile, Action<string> say)
    {
        this.dte = (DTE2)dte;
        this.workspace = workspace;
        this.projectFile = projectFile;
        // Evidence stays outside the Gradle root so it never counts as a build input.
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

    private string[] Snapshot(string file, int maxDepth = 9)
    {
        var lines = new List<string>();
        void Dump(UIHierarchyItem node, int depth)
        {
            lines.Add(new string(' ', depth * 2) + node.Name);
            if (depth >= maxDepth) return;
            // DTE reports zero children until a node is expanded.
            node.UIHierarchyItems.Expanded = true;
            foreach (UIHierarchyItem child in node.UIHierarchyItems) Dump(child, depth + 1);
        }
        Dump(ProjectNode(), 0);
        File.WriteAllLines(Path.Combine(evidence, file), lines);
        return lines.ToArray();
    }

    private void WaitForBuild()
    {
        Wait(() => dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateDone, 300000);
        Thread.Sleep(500);
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

    private string BuildPane(string file)
    {
        var document = dte.ToolWindows.OutputWindow.OutputWindowPanes.Item("Build").TextDocument;
        var text = document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        File.WriteAllText(Path.Combine(evidence, file), text);
        return text;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    private struct Rect { public int Left, Top, Right, Bottom; }

    private void Screenshot(string file)
    {
        SetProcessDPIAware();
        var handle = (IntPtr)dte.MainWindow.HWnd;
        SetForegroundWindow(handle);
        Thread.Sleep(1000);
        GetWindowRect(handle, out var rect);
        using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(evidence, file), ImageFormat.Png);
        }
    }

    public int Run()
    {
        dte.MainWindow.Activate();
        dte.ExecuteCommand("View.SolutionExplorer");
        var project = ProjectNode();
        Check(project != null && project.Name == Path.GetFileName(workspace), "Solution Explorer loaded the .kotlinproj as project '" + project?.Name + "'");
        var before = Snapshot("tree-before-import.txt", 3);
        Check(before.Count(l => l.Contains("Gradle model not imported")) == 1, "Unimported project shows the import hint instead of invented dependencies");

        // Id 0x108 was reused for another tool (Compose preview); only the Project Explorer command must be gone.
        Check(dte.Commands.Cast<Command>().All(c => (c.Name ?? "").IndexOf("ProjectExplorer", StringComparison.OrdinalIgnoreCase) < 0),
            "The Kotlin Project Explorer tool window command is gone");
        Check(HasCommand(0x10A) && HasCommand(0x200), "Tools > Kotlin: Create or Open .kotlinproj and the project context-menu commands are registered");
        // Project context-menu command: it is visible only while a Kotlin project is selected in Solution Explorer.
        project.Select(vsUISelectionType.vsUISelectionTypeSelect);
        var import = dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", 0x200);
        bool Imported() { try { return File.ReadAllText(projectFile).Contains("<ImportedAtUtc>"); } catch (IOException) { return false; } }
        dte.ExecuteCommand(import.Name);
        Wait(Imported, 240000);
        if (!Imported())
        {
            // Environment guard: a Gradle client can hang creating its JDK loopback pipe; cancel through the IDE and retry once.
            say("NOTE: import did not finish in 4 minutes; cancelling via Kotlin: Cancel Gradle and retrying once.");
            dte.ExecuteCommand(dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", 0x103).Name);
            Thread.Sleep(10000);
            dte.ExecuteCommand(import.Name);
            Wait(Imported, 300000);
        }
        Check(File.ReadAllText(projectFile).Contains("<SourceRoot Include="), "In-IDE '" + import.Name + "' imported the Gradle model with source roots");
        Thread.Sleep(3000);
        var tree = Snapshot("tree-after-import.txt");
        var trimmed = tree.Select(l => l.Trim()).ToArray();
        foreach (var expected in new[] { "app", "shared", "main", "src/main/kotlin", "Main.kt", "Greeting.kt", "Dependencies", "Gradle Scripts", "settings.gradle.kts", "build.gradle.kts (app)",
            "Toolchain", "Gradle Wrapper 8.14.7", "Frameworks", "Kotlin/JVM Gradle plugin 2.4.21" })
            Check(trimmed.Contains(expected), "Solution Explorer tree contains '" + expected + "'");
        Check(trimmed.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^runtimeClasspath \(\d+\)$")) >= 2, "Resolved runtimeClasspath configurations are listed per module");
        Check(trimmed.Contains("org.jetbrains.kotlin:kotlin-stdlib:2.4.21"), "Selected kotlin-stdlib 2.4.21 is shown as a direct dependency");
        Check(trimmed.Contains(":shared"), "Project dependency :shared is shown as a project reference");
        Check(trimmed.Contains("org.jetbrains:annotations:13.0  (transitive)"), "Transitive dependency is marked");
        Check(!trimmed.Any(l => l.Contains("not imported")), "Import hint disappears after the metadata refresh");
        if (SkipHiddenFilesOption) say("SKIPPED: Show hidden files option step (-SkipHiddenFilesOption)");
        else RunHiddenFiles();
        project = ProjectNode();
        foreach (var module in new[] { "app", "shared" })
        {
            var dependencies = Find(project, module, "Dependencies");
            if (dependencies != null) dependencies.UIHierarchyItems.Expanded = false;
        }
        Find(project, "app", "main", "src/main/kotlin", "sample")?.Select(vsUISelectionType.vsUISelectionTypeSelect);
        Screenshot("solution-explorer.png");

        var mainFile = Path.Combine(workspace, "app", "src", "main", "kotlin", "sample", "Main.kt");
        Find(project, "app", "main", "src/main/kotlin", "sample", "Main.kt")!.Select(vsUISelectionType.vsUISelectionTypeSelect);
        Explorer.DoDefaultAction();
        Wait(() => string.Equals(dte.ActiveDocument?.FullName, mainFile, StringComparison.OrdinalIgnoreCase), 10000);
        Check(string.Equals(dte.ActiveDocument?.FullName, mainFile, StringComparison.OrdinalIgnoreCase), "Double-click opens Main.kt from Solution Explorer");
        var misc = dte.Solution.Projects.Cast<Project>().FirstOrDefault(p => p.Name == "Miscellaneous Files");
        var inMisc = misc != null && misc.ProjectItems.Cast<ProjectItem>().Any(i => string.Equals(i.FileNames[1], mainFile, StringComparison.OrdinalIgnoreCase));
        Check(!inMisc, "Opened source belongs to the Kotlin project, not Miscellaneous Files");
        var original = File.ReadAllBytes(mainFile);
        var text = (TextDocument)dte.ActiveDocument!.Object("TextDocument");
        text.EndPoint.CreateEditPoint().Insert("// vistudio save check\r\n");
        dte.ActiveDocument.Save();
        Check(File.ReadAllText(mainFile).Contains("// vistudio save check"), "Saving an edited project document writes it through the project hierarchy");
        dte.ExecuteCommand("Edit.Undo");
        dte.ActiveDocument.Save();
        Check(File.ReadAllBytes(mainFile).SequenceEqual(original), "Undo and save restore the exact original bytes");

        var build = dte.Solution.SolutionBuild;
        build.Build(true); WaitForBuild();
        var pane = BuildPane("build-output.txt");
        Check(build.LastBuildInfo == 0 && pane.Contains("BUILD SUCCESSFUL") && pane.Contains("Gradle build in"), "Solution Build runs Gradle build and succeeds");
        Check(File.Exists(Path.Combine(workspace, "app", "build", "classes", "kotlin", "main", "sample", "MainKt.class"))
            && Directory.Exists(Path.Combine(workspace, "shared", "build", "classes", "kotlin", "main")), "Gradle compiled both Kotlin modules");
        build.Clean(true); WaitForBuild();
        pane = BuildPane("clean-output.txt");
        Check(build.LastBuildInfo == 0 && pane.Contains("Gradle clean in") && !Directory.Exists(Path.Combine(workspace, "app", "build")), "Solution Clean runs Gradle clean and removes outputs");
        dte.ExecuteCommand("Build.RebuildSolution"); Thread.Sleep(1000); WaitForBuild();
        pane = BuildPane("rebuild-output.txt");
        Check(build.LastBuildInfo == 0 && pane.Contains("BUILD SUCCESSFUL") && pane.Contains(":clean")
            && File.Exists(Path.Combine(workspace, "app", "build", "classes", "kotlin", "main", "sample", "MainKt.class")), "Solution Rebuild cleans and rebuilds through Gradle");

        var greeting = Path.Combine(workspace, "shared", "src", "main", "kotlin", "sample", "Greeting.kt");
        var greetingText = File.ReadAllText(greeting);
        File.WriteAllText(greeting, greetingText + "\nval vistudioBroken: Int = \"not a number\"\n");
        build.Build(true); WaitForBuild();
        pane = BuildPane("failed-build-output.txt");
        Check(build.LastBuildInfo == 1 && pane.Contains("BUILD FAILED"), "A real Kotlin compile error fails the solution build");
        var errors = new List<string>();
        Wait(() =>
        {
            var items = dte.ToolWindows.ErrorList.ErrorItems;
            for (var i = 1; i <= items.Count; i++)
            {
                var entry = items.Item(i);
                if (string.Equals(entry.FileName, greeting, StringComparison.OrdinalIgnoreCase)) errors.Add(entry.Description + " @" + entry.Line);
            }
            return errors.Count != 0;
        }, 10000);
        File.WriteAllLines(Path.Combine(evidence, "error-list.txt"), errors);
        Check(errors.Count != 0, "Error List shows the compiler error in Greeting.kt: " + string.Join(" | ", errors));
        dte.ExecuteCommand("View.ErrorList");
        Screenshot("error-list.png");
        File.WriteAllText(greeting, greetingText);
        build.Build(true); WaitForBuild();
        Check(build.LastBuildInfo == 0, "Build succeeds again after the fix");

        var added = Path.Combine(workspace, "shared", "src", "main", "kotlin", "sample", "AddedLater.kt");
        File.WriteAllText(added, "package sample\n\nfun addedLater() = 1\n");
        UIHierarchyItem found = null;
        Wait(() => (found = Find(ProjectNode(), "shared", "main", "src/main/kotlin", "sample", "AddedLater.kt")) != null, 10000);
        Check(found != null, "A Kotlin file created on disk appears in Solution Explorer without reloading");
        File.Delete(added);
        Wait(() => Find(ProjectNode(), "shared", "main", "src/main/kotlin", "sample", "AddedLater.kt") == null, 10000);
        Check(Find(ProjectNode(), "shared", "main", "src/main/kotlin", "sample", "AddedLater.kt") == null, "A deleted file disappears from Solution Explorer");

        build.Build(true); WaitForBuild();
        BuildPane("after-refresh-build.txt");
        Check(build.LastBuildInfo == 0, "Build after the add/delete round trip succeeds");
        build.Build(true); WaitForBuild();
        pane = BuildPane("up-to-date-build.txt");
        Check(build.LastBuildInfo == 0 && pane.Contains("1 up-to-date") && !pane.Contains("Gradle build in"), "A Build without input changes is reported up to date without starting Gradle");

        if (SkipDebug) say("SKIPPED: F5 and Ctrl+F5 (language server unavailable; -SkipDebug)");
        else RunDebug(mainFile);
        if (FailingServerPath != null) RunFailFast(FailingServerPath, mainFile);
        RunProperties();
        if (FolderWorkspace != null) RunCreateOrOpen(FolderWorkspace);
        return passed;
    }

    /// <summary>
    /// F5 with a language server that exits at startup must report the exit reason at once (T20) instead of waiting for
    /// the 3-minute initialization timeout. The configured server is restored afterwards.
    /// </summary>
    private void RunFailFast(string failingServer, string mainFile)
    {
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "settings.xml");
        var original = File.ReadAllBytes(settings);
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(settings);
            xml.Root!.SetElementValue("LanguageServerPath", failingServer);
            xml.Save(settings);
            dte.ExecuteCommand(dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", 0x104).Name); // Kotlin: Restart Language Server
            Wait(() => PaneText("Kotlin").Contains("Fix the server in Tools > Options > Kotlin"), 120000);
            Check(PaneText("Kotlin").Contains("Fix the server in Tools > Options > Kotlin"), "The failing server's unexpected exit is recorded with its reason");
            foreach (Document document in dte.Documents) document.Close(vsSaveChanges.vsSaveChangesNo);
            // The launch reports through a modal message box; DTE calls are rejected while it is open, so everything
            // until it is dismissed goes through UI Automation with the window handle taken beforehand.
            var main = System.Windows.Automation.AutomationElement.FromHandle((IntPtr)dte.MainWindow.HWnd);
            var started = DateTime.UtcNow;
            dte.ExecuteCommand("Debug.Start");
            System.Windows.Automation.AutomationElement? box = null;
            string text = "";
            Wait(() =>
            {
                foreach (System.Windows.Automation.AutomationElement window in main.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Window)))
                {
                    var content = string.Join(" ", window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text))
                        .Cast<System.Windows.Automation.AutomationElement>().Select(e => e.Current.Name));
                    if (content.Contains("exited with code")) { box = window; text = content; return true; }
                }
                return false;
            }, 120000);
            var seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
            File.WriteAllText(Path.Combine(evidence, "fail-fast-message.txt"), seconds + " s" + Environment.NewLine + text);
            Check(box != null && seconds < 60 && text.Contains("expired"), "F5 with an exited language server reports the reason after " + seconds + " s: " + text);
            var ok = box?.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "OK"));
            if (ok != null && ok.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var invoke)) ((System.Windows.Automation.InvokePattern)invoke).Invoke();
            Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 30000);
            Check(dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, "No debug session is left behind after the failed launch");
        }
        finally
        {
            File.WriteAllBytes(settings, original);
            try { dte.ExecuteCommand(dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", 0x104).Name); } catch (COMException) { }
        }
    }

    private void RunDebug(string mainFile)
    {
        // F5: the project is the startup project; its configuration resolves sample.MainKt and launches the Kotlin/JVM engine.
        foreach (Document document in dte.Documents) document.Close(vsSaveChanges.vsSaveChangesNo);
        dte.Debugger.Breakpoints.Add(File: mainFile, Line: 3);
        var started = DateTime.UtcNow;
        dte.ExecuteCommand("Debug.Start");
        Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, 600000);
        Check(dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, "F5 on the Kotlin project hits the breakpoint (" + (int)(DateTime.UtcNow - started).TotalSeconds + " s after F5)");
        var frame = dte.Debugger.CurrentStackFrame;
        var line = ((TextSelection)dte.ActiveDocument!.Selection).CurrentLine;
        File.WriteAllText(Path.Combine(evidence, "debug-break.txt"), "Function: " + frame.FunctionName + Environment.NewLine + "Document: " + dte.ActiveDocument.FullName + Environment.NewLine + "Line: " + line + Environment.NewLine);
        Check(frame.FunctionName.Contains("main") && string.Equals(dte.ActiveDocument.FullName, mainFile, StringComparison.OrdinalIgnoreCase) && line == 3,
            "Stopped in main at Main.kt:3 (" + frame.FunctionName + ")");
        Screenshot("debug-break.png");
        dte.Debugger.Go(false);
        Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 180000);
        Check(dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, "Continuing runs the program to completion");
        var kotlin = PaneText("Kotlin");
        File.WriteAllText(Path.Combine(evidence, "kotlin-output.txt"), kotlin);
        Check(kotlin.Contains("Kotlin debug target: sample.MainKt") && kotlin.Contains("Kotlin project is up to date; launching without another Gradle build."),
            "F5 resolved sample.MainKt from the project and launched the up-to-date build without another Gradle run");
        var debugOutput = PaneText("Debug");
        File.WriteAllText(Path.Combine(evidence, "debug-output.txt"), debugOutput);
        Check(debugOutput.Contains("Resolved Gradle import works"), "Program output appears in the Debug pane");

        dte.Debugger.Breakpoints.Item(1).Delete();
        dte.ExecuteCommand("Debug.StartWithoutDebugging");
        Wait(() => PaneText("Kotlin").Contains("Kotlin run target: sample.MainKt"), 60000);
        Wait(() => dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode, 300000);
        Wait(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 180000);
        var runOutput = PaneText("Debug");
        File.WriteAllText(Path.Combine(evidence, "run-output.txt"), runOutput);
        Check(PaneText("Kotlin").Contains("Kotlin run target: sample.MainKt") && runOutput.Contains("Resolved Gradle import works") && dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode,
            "Ctrl+F5 runs the program without debugging and returns to design mode");
    }

    private void RunProperties()
    {
        // Properties window (F4): the project node's settings are edited in the native property grid.
        // Selection reaches the Properties window only from the active Solution Explorer window.
        Explorer.Parent.Activate();
        ProjectNode().Select(vsUISelectionType.vsUISelectionTypeSelect);
        dte.ExecuteCommand("View.PropertiesWindow");
        Thread.Sleep(1500);
        const string arguments = "alpha \"beta gamma\"";
        SetGridValue("Arguments", arguments);
        Wait(() => KotlinProperty("DebugArguments") == arguments, 10000);
        Check(KotlinProperty("DebugArguments") == arguments, "Editing Arguments in the Properties window saves DebugArguments to the .kotlinproj");
        SetGridValue("Test tasks", ":app:test");
        Wait(() => KotlinProperty("TestTasks") == ":app:test", 10000);
        Check(KotlinProperty("TestTasks") == ":app:test", "Editing Test tasks in the Properties window saves TestTasks");
        Wait(() => GridValue("Test tasks") == ":app:test" && GridValue("Arguments") == arguments, 10000);
        Check(GridValue("Test tasks") == ":app:test" && GridValue("Arguments") == arguments, "The Properties window shows the saved values after the project reloads its metadata");
        var jdk = KotlinProperty("JdkHome");
        Check(GridValue("Gradle JDK") == jdk && GridValue("Gradle version") == "8.14.7", "Gradle JDK and last-import values are shown in the Properties window");
        Screenshot("properties-window.png");
        SetGridValue("Arguments", "");
        Wait(() => KotlinProperty("DebugArguments") == "", 10000);
        Check(KotlinProperty("DebugArguments") == "", "Clearing a property writes an empty value");
    }

    private bool HasCommand(int id)
    {
        try { return dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", id) != null; }
        catch (ArgumentException) { return false; }
        catch (COMException) { return false; }
    }

    private string KotlinProperty(string name)
    {
        try
        {
            var group = System.Xml.Linq.XDocument.Load(projectFile).Root!.Elements("PropertyGroup").First(e => (string?)e.Attribute("Label") == "Vistudio");
            return (string?)group.Element(name) ?? "";
        }
        catch (IOException) { return "<locked>"; }
        catch (System.Xml.XmlException) { return "<partial>"; }
    }

    // The Visual Studio Properties window is a WinForms PropertyGrid; its rows are UI Automation elements named after the property.
    private System.Windows.Automation.AutomationElement GridRow(string name)
    {
        var window = System.Windows.Automation.AutomationElement.FromHandle((IntPtr)dte.MainWindow.HWnd);
        System.Windows.Automation.AutomationElement? row = null;
        Wait(() =>
        {
            row = window.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, name))
                .Cast<System.Windows.Automation.AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out _));
            return row != null;
        }, 15000);
        return row ?? throw new Exception("Properties window row not found: " + name);
    }

    private string GridValue(string name)
    {
        return ((System.Windows.Automation.ValuePattern)GridRow(name).GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value;
    }

    private void SetGridValue(string name, string text)
    {
        ((System.Windows.Automation.ValuePattern)GridRow(name).GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).SetValue(text);
    }

    /// <summary>
    /// Dot-prefixed entries are hidden by default; Tools > Options > Kotlin > Show hidden files shows them and hides them again
    /// in the loaded project without reopening it. Only non-Kotlin files are created, so later Gradle builds are unaffected.
    /// </summary>
    private void RunHiddenFiles()
    {
        var root = Path.Combine(workspace, "app", "src", "main", "kotlin");
        var dotFolder = Path.Combine(root, ".vistudio-hidden");
        var dotFile = Path.Combine(root, ".vistudio-notes");
        var settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "settings.xml");
        var originalSettings = File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
        string[] Names()
        {
            try
            {
                var node = Find(ProjectNode(), "app", "main", "src/main/kotlin");
                if (node == null) return new string[0];
                node.UIHierarchyItems.Expanded = true;
                return node.UIHierarchyItems.Cast<UIHierarchyItem>().Select(i => i.Name).ToArray();
            }
            catch (COMException) { return new string[0]; }
        }
        Directory.CreateDirectory(dotFolder);
        File.WriteAllText(Path.Combine(dotFolder, "inside.txt"), "content of a dot folder");
        File.WriteAllText(dotFile, "dot file");
        try
        {
            Thread.Sleep(3000);
            var names = Names();
            Check(names.Contains("sample") && !names.Any(n => n.StartsWith(".")), "By default Solution Explorer hides dot files and folders: " + string.Join(", ", names));
            SetShowHiddenFiles(true);
            Wait(() => Names().Contains(".vistudio-hidden") && Names().Contains(".vistudio-notes"), 20000);
            names = Names();
            Check(names.SequenceEqual(new[] { ".vistudio-hidden", "sample", ".vistudio-notes" }),
                "Show hidden files lists dot folders and files in the open project without reopening it: " + string.Join(", ", names));
            Check(Find(ProjectNode(), "app", "main", "src/main/kotlin", ".vistudio-hidden", "inside.txt") != null, "A dot folder expands to its files");
            Screenshot("hidden-files-shown.png");
            SetShowHiddenFiles(false);
            Wait(() => !Names().Any(n => n.StartsWith(".")), 20000);
            names = Names();
            Check(names.Contains("sample") && !names.Any(n => n.StartsWith(".")), "Turning the option off hides them again: " + string.Join(", ", names));
        }
        finally
        {
            Directory.Delete(dotFolder, true);
            File.Delete(dotFile);
            // The option lives in the user's global settings file; restore it exactly as it was before the check.
            if (originalSettings != null) File.WriteAllBytes(settingsFile, originalSettings);
            else if (File.Exists(settingsFile)) File.Delete(settingsFile);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx context);

    private static object? FindDte(int processId)
    {
        GetRunningObjectTable(0, out var table);
        table.EnumRunning(out var monikers);
        var current = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
        while (monikers.Next(1, current, IntPtr.Zero) == 0)
        {
            CreateBindCtx(0, out var context);
            current[0].GetDisplayName(context, null, out var name);
            if (name.StartsWith("!VisualStudio.DTE.") && name.EndsWith(":" + processId)) { table.GetObject(current[0], out var found); return found; }
        }
        return null;
    }

    /// <summary>Changes the option through the real Tools > Options page (property grid + OK), the same path a user takes.</summary>
    private void SetShowHiddenFiles(bool value)
    {
        // Tools.Options is modal and blocks its caller until the dialog closes, so it runs on a worker thread. The worker takes
        // its own DTE proxy from the running object table: calling through this STA thread's proxy would wait for this thread,
        // which does not pump COM messages while it waits for the dialog.
        GetWindowThreadProcessId((IntPtr)dte.MainWindow.HWnd, out var processId);
        string? note = null;
        var worker = new Thread(() =>
        {
            try { ((DTE2)FindDte(processId)!).ExecuteCommand("Tools.Options", "9661d77a-63ae-494d-9699-9e6df41fc601"); }
            catch (Exception e) { note = "NOTE: Tools.Options returned: " + e.Message; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        var mainWindow = (IntPtr)dte.MainWindow.HWnd;
        // UI Automation runs on an MTA thread too: from this STA thread, ValuePattern.SetValue on the Options property grid
        // did not return until the dialog closed (seen on Visual Studio 18.12; the same call from an MTA thread takes ~20 ms).
        // The worker records its checks; they are reported here, because the logging callback belongs to this thread.
        Exception? failure = null;
        var results = new List<KeyValuePair<bool, string>>();
        var automation = new Thread(() =>
        {
            try { ChangeOption(mainWindow, value, results); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        automation.SetApartmentState(ApartmentState.MTA);
        automation.Start();
        Check(automation.Join(120000), "The Options dialog interaction finished within 120 s");
        lock (results) foreach (var result in results) Check(result.Key, result.Value);
        if (failure != null) throw new Exception("Options dialog automation failed: " + failure.Message, failure);
        Check(worker.Join(30000), "The Options dialog closed with OK");
        if (note != null) say(note);
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "settings.xml");
        Check(File.Exists(settings) && File.ReadAllText(settings).Contains("<ShowHiddenFiles>" + (value ? "true" : "false") + "</ShowHiddenFiles>"),
            "OK saved ShowHiddenFiles=" + value.ToString().ToLowerInvariant() + " to the Vistudio settings file");
    }

    private static bool Record(List<KeyValuePair<bool, string>> results, bool condition, string message)
    {
        lock (results) results.Add(new KeyValuePair<bool, string>(condition, message));
        return condition;
    }

    /// <summary>Finds the open Options dialog, sets 'Show hidden files' in its property grid and presses OK. Runs on an MTA thread.</summary>
    private void ChangeOption(IntPtr mainWindow, bool value, List<KeyValuePair<bool, string>> results)
    {
        var main = System.Windows.Automation.AutomationElement.FromHandle(mainWindow);
        System.Windows.Automation.AutomationElement? dialog = null, row = null;
        Wait(() =>
        {
            dialog = main.FindFirst(System.Windows.Automation.TreeScope.Children, new System.Windows.Automation.AndCondition(
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "#32770"),
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Options")));
            row = dialog?.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Show hidden files"))
                .Cast<System.Windows.Automation.AutomationElement>()
                .FirstOrDefault(e => e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out _));
            return row != null;
        }, 30000);
        if (!Record(results, dialog != null && row != null, "Tools > Options opens the Kotlin page with the 'Show hidden files' option")) return;
        var pattern = (System.Windows.Automation.ValuePattern)row!.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern);
        pattern.SetValue(value ? "True" : "False");
        Thread.Sleep(500);
        if (!Record(results, pattern.Current.Value == (value ? "True" : "False"), "The option grid shows Show hidden files = " + pattern.Current.Value)) return;
        // OK is the Win32 child button with control id 1 (IDOK). VS 18 exposes it to UI Automation as a pattern-less Pane of
        // class "Button", so it is found by id and window class and clicked with BM_CLICK on its window handle.
        var ok = dialog!.FindFirst(System.Windows.Automation.TreeScope.Children, new System.Windows.Automation.AndCondition(
            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "1"),
            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Button")));
        if (!Record(results, ok != null && ok.Current.NativeWindowHandle != 0, "The Options dialog has an OK button")) return;
        PostMessage((IntPtr)ok!.Current.NativeWindowHandle, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Open Folder, then Tools > Kotlin: Create or Open .kotlinproj creates metadata, opens it as a project and imports it.</summary>
    private void RunCreateOrOpen(string folder)
    {
        dte.Solution.Close(false);
        Wait(() => !dte.Solution.IsOpen, 30000);
        dte.ExecuteCommand("File.OpenFolder", "\"" + folder + "\"");
        Thread.Sleep(15000);
        var created = Path.Combine(folder, "vistudio.kotlinproj");
        Check(!File.Exists(created), "The Open Folder copy starts without .kotlinproj metadata");
        dte.ExecuteCommand(dte.Commands.Item("{75625a1a-f6e1-42aa-bf3f-90432c756124}", 0x10A).Name);
        bool Imported() { try { return File.Exists(created) && File.ReadAllText(created).Contains("<ImportedAtUtc>"); } catch (IOException) { return false; } }
        Wait(Imported, 300000);
        Check(Imported() && File.ReadAllText(created).Contains("<SourceRoot Include="), "Create or Open generated vistudio.kotlinproj and imported the Gradle model");
        UIHierarchyItem? node = null;
        Wait(() =>
        {
            try { node = ProjectNode(); return node.Name == Path.GetFileName(folder) && Find(node, "app", "main", "src/main/kotlin", "sample", "Main.kt") != null; }
            catch (Exception) { return false; }
        }, 60000);
        Check(node != null && node.Name == Path.GetFileName(folder), "The folder now opens as the Kotlin project '" + node?.Name + "' in Solution Explorer with its sources");
        Screenshot("create-or-open.png");
    }
}
