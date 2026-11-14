using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EnvDTE;
using EnvDTE80;

/// <summary>
/// Native Visual Studio checks for the language project types and the Packages nodes (T59, T60), driven through DTE
/// only: New Project templates, the Solution Explorer tree, solution Build, package commands and F5 in the native engine.
/// Compiled by tests/verify-language-projects.ps1 against the instance's own interop assembly.
/// </summary>
public sealed class LanguageProjectsAcceptance
{
    private readonly DTE dte;
    private readonly Action<string> say;
    private int passed;

    public LanguageProjectsAcceptance(object dte, Action<string> say)
    {
        this.dte = (DTE)dte;
        this.say = say;
    }

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

    /// <summary>Creates a project from a language template into <paramref name="folder"/>; returns its project file.</summary>
    public string Create(string templateName, string folder, string name, string extension)
    {
        var solution = (Solution2)dte.Solution;
        if (!solution.IsOpen) solution.Create(folder, "Acceptance");
        var template = solution.GetProjectTemplate(templateName + ".zip", "VistudioLanguages");
        Check(File.Exists(template), templateName + " is installed as a New Project template (" + template + ")");
        var destination = Path.Combine(folder, name);
        try { solution.AddFromTemplate(template, destination, name, false); }
        catch (Exception e) { say("AddFromTemplate: " + e.Message); }
        var file = Path.Combine(destination, name + extension);
        Check(WaitFor(() => File.Exists(file), 60), templateName + " created " + file);
        Check(WaitFor(() => Projects().Any(p => SamePath(p.FullName, file)), 60), name + " is loaded in the solution");
        return file;
    }

    public IEnumerable<Project> Projects() => dte.Solution.Projects.Cast<Project>();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private UIHierarchy Explorer()
    {
        var window = dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer);
        window.Activate();
        return (UIHierarchy)window.Object;
    }

    /// <summary>The Solution Explorer item of a project by caption, expanded.</summary>
    public UIHierarchyItem ProjectItem(string caption)
    {
        UIHierarchyItem found = null;
        Check(WaitFor(() =>
        {
            var solutionNode = Explorer().UIHierarchyItems.Item(1);
            solutionNode.UIHierarchyItems.Expanded = true;
            found = solutionNode.UIHierarchyItems.Cast<UIHierarchyItem>().FirstOrDefault(i => i.Name == caption);
            return found != null;
        }, 60), "Solution Explorer shows project " + caption);
        found.UIHierarchyItems.Expanded = true;
        return found;
    }

    // Nodes arrive from PowerShell as plain COM objects, which it cannot convert to the interop interface itself.
    public string[] Children(object node)
    {
        var item = (UIHierarchyItem)node;
        item.UIHierarchyItems.Expanded = true;
        return item.UIHierarchyItems.Cast<UIHierarchyItem>().Select(i => i.Name).ToArray();
    }

    /// <summary>Waits until a child caption matches; returns it.</summary>
    public UIHierarchyItem WaitChild(object node, Func<string, bool> match, int seconds, string what)
    {
        var parent = (UIHierarchyItem)node;
        UIHierarchyItem found = null;
        var ok = WaitFor(() =>
        {
            parent.UIHierarchyItems.Expanded = true;
            found = parent.UIHierarchyItems.Cast<UIHierarchyItem>().FirstOrDefault(i => match(i.Name));
            return found != null;
        }, seconds);
        Check(ok, what + (ok ? ": " + found.Name : " (children: " + string.Join(" | ", Children(parent)) + ")"));
        return found;
    }

    /// <summary>
    /// Waits for a child of the project's child whose caption starts with <paramref name="parentPrefix"/>; the parent is
    /// looked up again on every poll, because DTE drops its item objects when a caption changes.
    /// </summary>
    public UIHierarchyItem WaitNested(object project, string parentPrefix, Func<string, bool> match, int seconds, string what)
    {
        var root = (UIHierarchyItem)project;
        UIHierarchyItem found = null;
        string seen = "";
        var ok = WaitFor(() =>
        {
            root.UIHierarchyItems.Expanded = true;
            var parent = root.UIHierarchyItems.Cast<UIHierarchyItem>().FirstOrDefault(i => i.Name.StartsWith(parentPrefix, StringComparison.Ordinal));
            if (parent == null) return false;
            parent.UIHierarchyItems.Expanded = true;
            var children = parent.UIHierarchyItems.Cast<UIHierarchyItem>().ToList();
            seen = parent.Name + ": " + string.Join(" | ", children.Select(c => c.Name));
            found = children.FirstOrDefault(c => match(c.Name));
            return found != null;
        }, seconds);
        Check(ok, what + (ok ? ": " + found.Name : " (" + seen + ")"));
        return found;
    }

    public void Execute(object node, string command)
    {
        var item = (UIHierarchyItem)node;
        // Command state follows the active window's selection: Visual Studio in front, Solution Explorer active.
        dte.MainWindow.Activate();
        dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer).Activate();
        item.Select(vsUISelectionType.vsUISelectionTypeSelect);
        System.Threading.Thread.Sleep(500);
        var selected = ((object[])Explorer().SelectedItems).Cast<UIHierarchyItem>().Select(i => i.Name).ToArray();
        if (!selected.Contains(item.Name))
        {
            // UIHierarchyItem.Select left the previous selection on virtual nodes; selecting by path does reach them.
            var path = new List<string>();
            for (object current = item; current is UIHierarchyItem level; current = level.Collection.Parent) path.Insert(0, level.Name);
            Explorer().GetItem(string.Join("\\", path)).Select(vsUISelectionType.vsUISelectionTypeSelect);
            System.Threading.Thread.Sleep(500);
            selected = ((object[])Explorer().SelectedItems).Cast<UIHierarchyItem>().Select(i => i.Name).ToArray();
            say("Selected by path " + string.Join("\\", path));
        }
        say("Solution Explorer selection: " + string.Join(" | ", selected));
        if (!commandsListed)
        {
            commandsListed = true;
            var names = dte.Commands.Cast<Command>().Where(c => string.Equals(c.Guid, PackageCommands, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ID.ToString("x") + "=" + c.Name).ToArray();
            say("Package commands known to DTE: " + string.Join(", ", names));
        }
        say("Available on " + item.Name + ": " + string.Join(", ", CommandIds.Select(c => c.Key + "=" + dte.Commands.Item(PackageCommands, c.Value).IsAvailable)));
        // By id: the command table entry is what Solution Explorer's context menu invokes.
        object input = null, output = null;
        dte.Commands.Raise(PackageCommands, CommandIds[command], ref input, ref output);
        say("Executed " + command + " on " + item.Name);
    }

    private const string PackageCommands = "{c386fa11-6735-43f2-9cfe-2dec11a8d70a}";
    private static readonly Dictionary<string, int> CommandIds = new Dictionary<string, int>
    {
        ["Vistudio.AddPackage"] = 0x100, ["Vistudio.CheckPackageUpdates"] = 0x101, ["Vistudio.UpdateAllPackages"] = 0x102,
        ["Vistudio.UpdateAllPackagesToLatest"] = 0x103, ["Vistudio.UpdatePackage"] = 0x110, ["Vistudio.UpdatePackageToLatest"] = 0x111,
        ["Vistudio.RemovePackage"] = 0x112, ["Vistudio.RunLanguageProject"] = 0x120, ["Vistudio.TestLanguageProject"] = 0x121,
    };
    private bool commandsListed;

    /// <summary>Solution build; true when no project failed.</summary>
    public bool Build(int seconds)
    {
        var build = dte.Solution.SolutionBuild;
        build.Build(false);
        WaitFor(() => build.BuildState == vsBuildState.vsBuildStateDone, seconds);
        Check(build.BuildState == vsBuildState.vsBuildStateDone, "Solution build finished");
        say("Build output:\n" + Pane("Build"));
        return build.LastBuildInfo == 0;
    }

    public string Pane(string name)
    {
        foreach (OutputWindowPane pane in ((DTE2)dte).ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (pane.Name != name) continue;
            var document = pane.TextDocument;
            return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
        }
        return "";
    }

    /// <summary>Set as Startup Project from Solution Explorer, as a user would.</summary>
    public void SetStartup(string caption)
    {
        var item = ProjectItem(caption);
        dte.MainWindow.Activate();
        dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer).Activate();
        item.Select(vsUISelectionType.vsUISelectionTypeSelect);
        System.Threading.Thread.Sleep(500);
        dte.ExecuteCommand("Project.SetasStartUpProject");
        var startup = (object[])dte.Solution.SolutionBuild.StartupProjects;
        say("Startup projects: " + string.Join(", ", startup.Select(s => s.ToString())));
    }

    /// <summary>F5 with a breakpoint; returns the function of the frame it stopped in.</summary>
    public string DebugToBreakpoint(string file, int line, int seconds)
    {
        dte.Debugger.Breakpoints.Add("", file, line);
        dte.ExecuteCommand("Debug.Start");
        var stopped = WaitFor(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode, seconds);
        Check(stopped, "F5 stopped at a breakpoint in " + Path.GetFileName(file) + ":" + line);
        var frame = dte.Debugger.CurrentStackFrame;
        var function = frame.FunctionName;
        say("Stopped in " + function + " (" + frame.Language + "), locals: " + string.Join(", ", frame.Locals.Cast<Expression>().Select(l => l.Name + "=" + l.Value)));
        dte.Debugger.Stop(true);
        WaitFor(() => dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode, 30);
        dte.Debugger.Breakpoints.Cast<Breakpoint>().ToList().ForEach(b => b.Delete());
        return function;
    }

    public int Passed => passed;
}
