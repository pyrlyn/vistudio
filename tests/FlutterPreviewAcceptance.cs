// End-to-end Visual Studio acceptance for the Flutter widget preview, driven through typed EnvDTE calls and UI
// Automation (the tool window's controls carry FlutterPreview.* automation ids). Compiled by verify-flutter-preview.ps1.
// The previewer prints "Done loading previews" only after a page connected to its debug service, and each page posts
// "Connected" on DTD, so the Dart pane proves that the WebView2 pages in the tool window loaded the previewer.
using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Windows.Automation;
using EnvDTE;
using EnvDTE80;

public sealed class FlutterPreviewAcceptance
{
    private readonly DTE2 dte;
    private readonly Action<string> say;
    private int passed;
    private string evidence = Path.GetTempPath();
    private string root = "";
    private string flutterSdk = "";

    public FlutterPreviewAcceptance(object dte, Action<string> say)
    {
        this.dte = (DTE2)dte;
        this.say = say;
    }

    // ---- Checks and evidence ----

    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            say("FAIL: " + message);
            Capture("failure.png");
            try { File.WriteAllText(Path.Combine(evidence, "dart-pane.txt"), DartPane); } catch (Exception) { }
            throw new Exception(message);
        }
        passed++;
        say("PASS: " + message);
    }

    private static bool Wait(Func<bool> condition, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 500)
        {
            try { if (condition()) return true; }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (ElementNotAvailableException) { }
            System.Threading.Thread.Sleep(500);
        }
        try { return condition(); }
        catch (Exception) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>The Visual Studio main window as a PNG (PW_RENDERFULLCONTENT also captures the WebView2 content).</summary>
    private void Capture(string name)
    {
        var path = Path.Combine(evidence, name);
        try
        {
            IntPtr hwnd = dte.MainWindow.HWnd;
            GetWindowRect(hwnd, out var r);
            using (var bitmap = new System.Drawing.Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    PrintWindow(hwnd, hdc, 2);
                    graphics.ReleaseHdc(hdc);
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                // The tool window alone (it docks at the right edge of a large window).
                var status = ById("FlutterPreview.Status");
                var pane = status == null ? null : TreeWalker.ControlViewWalker.GetParent(TreeWalker.ControlViewWalker.GetParent(status));
                if (pane != null)
                {
                    var b = pane.Current.BoundingRectangle;
                    var area = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle((int)b.Left - r.Left, (int)b.Top - r.Top - 140, (int)b.Width, (int)b.Height + 140), new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    if (area.Width > 50 && area.Height > 50)
                        using (var crop = bitmap.Clone(area, bitmap.PixelFormat)) crop.Save(Path.ChangeExtension(path, null) + "-window.png", System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            say("Screenshot: " + path);
        }
        catch (Exception e) { say("Screenshot failed: " + e.Message); }
    }

    // ---- DTE helpers ----

    private string DartPane
    {
        get
        {
            foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
            {
                if (pane.Name != "Dart") continue;
                var document = pane.TextDocument;
                return document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
            }
            return "";
        }
    }

    private int Count(string text) => DartPane.Split('\n').Count(l => l.Contains(text));
    private string LastLine(string text) => DartPane.Split('\n').LastOrDefault(l => l.Contains(text))?.Trim() ?? "";

    private Document Open(string path)
    {
        dte.ItemOperations.OpenFile(path);
        Wait(() => dte.ActiveDocument != null && string.Equals(dte.ActiveDocument.FullName, path, StringComparison.OrdinalIgnoreCase), 30000);
        return dte.ActiveDocument;
    }

    private static string Text(Document document)
    {
        var text = (TextDocument)document.Object("TextDocument");
        return text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
    }

    private static void Replace(Document document, string oldText, string newText)
    {
        if (!Text(document).Contains(oldText)) throw new Exception("Text not found in " + document.Name + ": " + oldText);
        var text = (TextDocument)document.Object("TextDocument");
        text.StartPoint.CreateEditPoint().ReplacePattern(text.EndPoint, oldText, newText, (int)vsFindOptions.vsFindOptionsMatchCase);
    }

    private static void Caret(Document document, string lineStart, int column = 8)
    {
        var lines = Text(document).Replace("\r", "").Split('\n');
        ((TextSelection)document.Selection).MoveToLineAndOffset(Array.FindIndex(lines, l => l.StartsWith(lineStart)) + 1, column);
    }

    private void Execute(string command)
    {
        say("> " + command);
        dte.ExecuteCommand(command, "");
    }

    private bool Available(string command)
    {
        try { return dte.Commands.Item(command).IsAvailable; }
        catch (ArgumentException) { return false; }
    }

    private Window PreviewWindow() => dte.Windows.Cast<Window>().FirstOrDefault(w => { try { return w.Caption == "Flutter Widget Preview"; } catch (Exception) { return false; } });

    /// <summary>Processes whose command line runs the previewer, its daemon or its analysis server.</summary>
    private static string[] PreviewProcesses()
    {
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%widget-preview%' OR CommandLine LIKE '%tooling-daemon%'"))
            return searcher.Get().Cast<ManagementObject>().Select(o => o["ProcessId"] + " " + o["CommandLine"]).Where(c => !c.Contains("Win32_Process")).ToArray();
    }

    /// <summary>Rewrites the workspace's .vs\settings.json (the Dart settings of this window only; global settings stay untouched).</summary>
    private void WorkspaceSettings(string extra, string sdkPath = null)
    {
        var json = "{\n  \"vistudio.dart.flutterSdkPath\": \"" + (sdkPath ?? flutterSdk).Replace("\\", "/") + "\",\n  \"vistudio.dart.widgetPreviewVerbose\": true" + (extra.Length == 0 ? "" : ",\n  " + extra) + "\n}\n";
        File.WriteAllText(Path.Combine(root, ".vs", "settings.json"), json);
        say("Workspace settings: " + json.Replace("\n", " "));
        System.Threading.Thread.Sleep(1500);
    }

    // ---- UI Automation helpers (the tool window's WPF controls) ----

    private AutomationElement Main() => AutomationElement.FromHandle(dte.MainWindow.HWnd);

    private AutomationElement ById(string id) =>
        Main().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private string Name(string id) => ById(id)?.Current.Name ?? "";

    private void Invoke(string id)
    {
        var element = ById(id) ?? throw new Exception("No UI element " + id);
        say("> invoke " + id);
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private void Choose(string comboId, string item)
    {
        var combo = ById(comboId) ?? throw new Exception("No combo box " + comboId);
        say("> " + comboId + " = " + item);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        System.Threading.Thread.Sleep(500);
        var choice = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, item)) ?? throw new Exception("No item '" + item + "' in " + comboId);
        ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        try { expand.Collapse(); } catch (InvalidOperationException) { }
    }

    /// <summary>Selects a file in Solution Explorer (Open Folder view) through UI Automation, expanding its folders.</summary>
    private void SelectInSolutionExplorer(params string[] path)
    {
        dte.ExecuteCommand("View.SolutionExplorer", "");
        System.Threading.Thread.Sleep(1500);
        var tree = Main();
        AutomationElement scope = tree;
        for (var i = 0; i < path.Length; i++)
        {
            var name = path[i];
            AutomationElement item = null;
            Wait(() => (item = scope.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.NameProperty, name))).Cast<AutomationElement>().FirstOrDefault()) != null, 15000);
            if (item == null) throw new Exception("tree item " + name + " not found");
            if (i < path.Length - 1)
            {
                if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) ((ExpandCollapsePattern)expand).Expand();
                System.Threading.Thread.Sleep(1000);
                scope = item;
            }
            else
            {
                say("> select " + string.Join("\\", path) + " in Solution Explorer");
                ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                try { item.SetFocus(); } catch (InvalidOperationException) { }
                System.Threading.Thread.Sleep(1500);
            }
        }
    }

    // ---- The run ----

    public int Run(string sampleRoot, string evidenceFolder, string sdk)
    {
        root = sampleRoot;
        evidence = evidenceFolder;
        flutterSdk = sdk;
        try { dte.MainWindow.WindowState = vsWindowState.vsWindowStateMaximize; } catch (Exception) { }
        var card = Path.Combine(root, "lib", "widgets", "greeting_card.dart");
        var badge = Path.Combine(root, "lib", "widgets", "status_badge.dart");
        var plain = Path.Combine(root, "tools", "plain_dart", "lib", "plain.dart");

        // Registration: commands and options.
        foreach (var command in new[] { "Dart.FlutterPreviewWidget", "Dart.PreviewWidgetInEditor", "Dart.PreviewWidgetFile", "Dart.FlutterWidgetPreviewWindow" })
            Check(dte.Commands.Item(command) != null, "Command " + command + " is registered");
        var document = Open(card);
        Check(Wait(() => DartPane.Contains("Dart analysis server initialized."), 180000), "Dart analysis server running for the sample package");
        var options = dte.get_Properties("Dart", "General");
        var inUse = (string)options.Item("FlutterSdkInUse").Value;
        Check(inUse.Contains("Flutter 3.") && (bool)options.Item("WidgetPreview").Value, "Tools > Options > Dart shows the Flutter SDK in use and the widget preview settings: " + inUse);

        // Visibility: only for Dart files of Flutter packages.
        Check(Wait(() => Available("Dart.FlutterPreviewWidget"), 30000), "Flutter: Preview Widget is available in a Flutter package's .dart file");
        Open(plain);
        Check(Wait(() => !Available("Dart.FlutterPreviewWidget") && !Available("Dart.PreviewWidgetInEditor"), 30000), "Preview commands are hidden for a Dart package without Flutter (tools\\plain_dart)");
        document = Open(card);
        Check(Wait(() => Available("Dart.FlutterPreviewWidget"), 30000), "Available again in the Flutter file");

        // Start, with the saved window choice "Phone, Light | Dark, 1.3x, ar": two pages side by side.
        Execute("Dart.FlutterPreviewWidget");
        Check(Wait(() => PreviewWindow() != null && PreviewWindow().Visible, 30000), "Flutter: Preview Widget opened the Flutter Widget Preview tool window");
        Check(Wait(() => DartPane.Contains("Widget preview: Flutter "), 60000), "The preview found the Flutter SDK: " + LastLine("Widget preview: Flutter "));
        Check(Wait(() => DartPane.Contains("Widget preview: Previews loaded.") || DartPane.Contains("could not start"), 900000) && DartPane.Contains("Widget preview: Previews loaded."),
            "The previewer page loaded in the window's WebView2 (Done loading previews)");
        Check(PreviewProcesses().Length >= 3, "The previewer, its daemon and its analysis server are running: " + PreviewProcesses().Length + " processes");
        Check(Wait(() => Count("loaded in WebView2") >= 2, 120000), "Both configurations' pages loaded in WebView2: " + string.Join(" | ", DartPane.Split('\n').Where(l => l.Contains("loaded in WebView2")).Select(l => l.Trim())));
        Check(Wait(() => Count("Previewer page connected to DTD.") >= 2, 300000), "Both pages started the previewer app (" + Count("Previewer page connected to DTD.") + " connected)");
        Check(Name("FlutterPreview.Project") == "flutter_preview_sample", "The window shows the package name: " + Name("FlutterPreview.Project"));
        System.Threading.Thread.Sleep(10000);
        Capture("01-light-dark-phone.png");
        Caret(document, "@Preview(name: 'Greeting card'", 1);
        System.Threading.Thread.Sleep(3000);
        Capture("01b-codelens-on-previews.png");

        // Toolbar: a new choice rebuilds the pages and is saved at once.
        var connected = Count("Previewer page connected to DTD.");
        Choose("FlutterPreview.Theme", "Dark");
        Check(Wait(() => File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "flutter-preview-window.xml")).Contains("<Theme>dark</Theme>"), 10000),
            "The theme choice was saved immediately");
        // Pages are reused while the previewer runs: the color scheme changes live (no reload), the second page is parked.
        System.Threading.Thread.Sleep(5000);
        Check(Count("Previewer page connected to DTD.") == connected && !DartPane.Contains("color scheme of"), "Choosing Dark switched the color scheme in place, without reloading the page");
        Choose("FlutterPreview.Device", "Fit window");
        var reconfigured = Count("reconfigured in place");
        Choose("FlutterPreview.TextScale", "1.0\u00d7");
        Check(Wait(() => Count("reconfigured in place") > reconfigured, 30000), "Changing the text scale reconfigured the running page in place");
        reconfigured = Count("reconfigured in place");
        Choose("FlutterPreview.Locale", "System");
        Check(Wait(() => Count("reconfigured in place") > reconfigured, 30000), "Changing the locale reconfigured the running page in place");
        System.Threading.Thread.Sleep(5000);
        Check(Count("Previewer page connected to DTD.") == connected, "No page was reloaded for the configuration changes (the debug service keeps counting reloaded pages)");
        System.Threading.Thread.Sleep(10000);
        Capture("02-dark-fit.png");

        // Restart button: hot restart through the previewer's DTD service.
        // After the toolbar rebuilt the pages, the previewer's debug service may still count the closed ones; its hot restart
        // then times out and Vistudio reloads the pages instead (same result: the latest code, fresh state).
        var restarts = Count("Restarted application in");
        var fallbacks = Count("reloading the pages instead");
        var pagesBefore = Count("Previewer page connected to DTD.");
        Invoke("FlutterPreview.Restart");
        Check(Wait(() => DartPane.Contains("hot restart requested") && Count("Restarted application in") > restarts, 180000) && !DartPane.Contains("Hot restart failed"),
            "The Restart button hot restarted the previews (every page answered)");

        // Hot reload on save.
        var reloads = Count("Reloaded application in");
        Replace(document, "'Hello, $name!'", "'Hi from Visual Studio, $name!'");
        document.Save();
        Check(Wait(() => Count("Reloaded application in") > reloads, 180000), "Saving the widget file hot reloaded the previews");
        System.Threading.Thread.Sleep(8000);
        Capture("03-after-reload.png");

        // A code error: the window and the Dart pane name the previews that are not rendered.
        Replace(document, "final String name;", "final String name");
        document.Save();
        Check(Wait(() => DartPane.Contains("is not rendered") && Name("FlutterPreview.NoticeText").Contains("not rendered"), 180000),
            "A code error is reported in the window: " + Name("FlutterPreview.NoticeText"));
        Capture("04-code-error.png");
        reloads = Count("Reloaded application in");
        Replace(document, "final String name", "final String name;");
        document.Save();
        Check(Wait(() => Count("Reloaded application in") > reloads, 180000), "Fixing the error reloads again");
        Check(Wait(() => !Name("FlutterPreview.NoticeText").Contains("not rendered") || ById("FlutterPreview.NoticeText")!.Current.IsOffscreen, 60000), "The error notice is gone after the fix");

        // Reload = AfterDelay (this window's .vs\settings.json): typing alone saves and reloads after the pause.
        WorkspaceSettings("\"vistudio.dart.widgetPreviewReload\": \"AfterDelay\",\n  \"vistudio.dart.widgetPreviewReloadDelay\": 700");
        reloads = Count("Reloaded application in");
        Replace(document, "'Hi from Visual Studio, $name!'", "'Typed, $name!'");
        Check(Wait(() => document.Saved && Count("Reloaded application in") > reloads, 180000), "With Reload = AfterDelay the edit was saved after the pause and the previews reloaded without an explicit save");
        WorkspaceSettings("");

        // CodeLens on a widget class without @Preview (UI Automation, when Visual Studio exposes the CodeLens label).
        var badgeText = File.ReadAllText(badge);
        var badgeDocument = Open(badge);
        Caret(badgeDocument, "class StatusBadge");
        System.Threading.Thread.Sleep(20000); // CodeLens is computed after the view settles
        Capture("05-codelens.png");
        var hosts = Count("flutter-preview\\flutter_preview_sample-");
        var lens = Main().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Preview")).Cast<AutomationElement>()
            .FirstOrDefault(e => { try { return e.Current.AutomationId.Length == 0 || !e.Current.AutomationId.StartsWith("FlutterPreview."); } catch (Exception) { return false; } });
        if (lens != null && lens.TryGetCurrentPattern(InvokePattern.Pattern, out var lensInvoke))
        {
            say("> invoke CodeLens 'Preview'");
            ((InvokePattern)lensInvoke).Invoke();
            Check(Wait(() => Count("flutter-preview\\flutter_preview_sample-") > hosts, 60000), "The Preview CodeLens above StatusBadge previews it through the temporary host project");
        }
        else
        {
            say("INFO: the CodeLens label is not exposed to UI Automation" + (lens == null ? "" : " as invokable") + "; using the editor context menu command (see 05-codelens.png for the rendered lens)");
            Execute("Dart.PreviewWidgetInEditor");
            Check(Wait(() => Count("flutter-preview\\flutter_preview_sample-") > hosts, 60000), "Preview Widget in the editor (caret in StatusBadge) previews it through the temporary host project");
        }
        Check(Wait(() => DartPane.LastIndexOf("Widget preview: Previews loaded.") > DartPane.LastIndexOf("flutter-preview\\flutter_preview_sample-"), 900000),
            "The host project's previewer loaded in the window");
        Check(Name("FlutterPreview.NoticeText").Contains("has no @Preview") && ById("FlutterPreview.Notice.Add @Preview to source") != null,
            "The window explains the temporary project and offers Add @Preview to source: " + Name("FlutterPreview.NoticeText"));
        Check(File.ReadAllText(badge) == badgeText, "status_badge.dart was not changed by the temporary preview");
        Capture("06-host-project.png");

        // Solution Explorer: Preview Widget on a selected file (back to the package's own previews).
        var starts = Count("Widget preview: Flutter ");
        try
        {
            SelectInSolutionExplorer("lib", "widgets", "greeting_card.dart");
            Check(Wait(() => Available("Dart.PreviewWidgetFile"), 30000), "Preview Widget is available on a .dart file selected in Solution Explorer");
            Execute("Dart.PreviewWidgetFile");
            Check(Wait(() => Count("Widget preview: Flutter ") > starts && LastLine("Widget preview: Flutter ").EndsWith(root), 60000), "Preview Widget from Solution Explorer previews the package (" + LastLine("Widget preview: Flutter ") + ")");
            Check(Wait(() => DartPane.LastIndexOf("Widget preview: Previews loaded.") > DartPane.LastIndexOf("Widget preview: Flutter "), 900000), "The package previewer loaded again");
        }
        catch (Exception e) when (!(e.Message.StartsWith("Preview Widget"))) { say("INFO: Solution Explorer item not reachable through UI Automation (" + e.Message + ")"); }

        // Quick fix from the window: Add @Preview to source.
        badgeDocument = Open(badge);
        Caret(badgeDocument, "class StatusBadge");
        hosts = Count("flutter-preview\\flutter_preview_sample-");
        Execute("Dart.PreviewWidgetInEditor");
        Check(Wait(() => ById("FlutterPreview.Notice.Add @Preview to source") != null, 120000), "The Add @Preview to source action is offered");
        starts = Count("Widget preview: Flutter ");
        Invoke("FlutterPreview.Notice.Add @Preview to source");
        Check(Wait(() => File.ReadAllText(badge).Contains("@Preview(name: 'StatusBadge')") && File.ReadAllText(badge).Contains("Widget statusBadgePreview() => const StatusBadge();")
                         && File.ReadAllText(badge).Contains("import 'package:flutter/widget_previews.dart';"), 30000),
            "Add @Preview to source wrote the preview function and the import into status_badge.dart (and saved it)");
        Check(Wait(() => DartPane.LastIndexOf("Widget preview: Previews loaded.") > DartPane.LastIndexOf("Widget preview: Flutter ") && Count("Widget preview: Flutter ") > starts && LastLine("Widget preview: Flutter ").EndsWith(root), 900000),
            "After the fix the package's own previewer shows StatusBadge");
        Capture("07-quick-fix.png");

        // SDK errors: a configured folder without flutter.bat is reported with a way to fix it.
        WorkspaceSettings("", "C:/no/such/flutter");
        document = Open(card);
        Execute("Dart.FlutterPreviewWidget");
        Check(Wait(() => DartPane.Contains("configured Flutter SDK folder has no bin\\flutter.bat") && ById("FlutterPreview.Notice.Set Flutter SDK path") != null, 30000),
            "A wrong Flutter SDK folder is reported with Set Flutter SDK path...: " + Name("FlutterPreview.NoticeText"));
        Capture("08-sdk-error.png");
        WorkspaceSettings("");

        // Closing the window stops everything; the window command opens it again.
        PreviewWindow().Close(vsSaveChanges.vsSaveChangesNo);
        Check(Wait(() => DartPane.Contains("stopping (the preview window was closed)"), 30000), "Closing the window stops the previewer");
        Check(Wait(() => PreviewProcesses().Length == 0, 60000), "No previewer, daemon or analysis server process is left: " + string.Join(" | ", PreviewProcesses()));
        Execute("Dart.FlutterWidgetPreviewWindow");
        Check(Wait(() => PreviewWindow() != null && PreviewWindow().Visible, 30000), "Flutter Widget Preview Window opens the window again");

        // Closing the folder stops a running previewer.
        document = Open(card);
        var loaded = Count("Widget preview: Previews loaded.");
        Execute("Dart.FlutterPreviewWidget");
        Check(Wait(() => Count("Widget preview: Previews loaded.") > loaded, 900000), "Preview started again");
        // Open Folder: File > Close Folder; the solution close command closes the folder workspace too.
        try { Execute("File.CloseFolder"); }
        catch (System.Runtime.InteropServices.COMException e) { say("File.CloseFolder: " + e.Message + "; using File.CloseSolution"); Execute("File.CloseSolution"); }
        Check(Wait(() => DartPane.Contains("stopping (the folder or solution changed)"), 60000), "Closing the folder stops the previewer");
        Check(Wait(() => PreviewProcesses().Length == 0, 60000), "No process is left after closing the folder: " + string.Join(" | ", PreviewProcesses()));

        // Reopen and leave a previewer running: the script then quits Visual Studio and checks that nothing is left.
        dte.ExecuteCommand("File.OpenFolder", "\"" + root + "\"");
        System.Threading.Thread.Sleep(5000);
        document = Open(card);
        Check(Wait(() => Available("Dart.FlutterPreviewWidget"), 120000), "Preview available after reopening the folder");
        loaded = Count("Widget preview: Previews loaded.");
        Execute("Dart.FlutterPreviewWidget");
        Check(Wait(() => Count("Widget preview: Previews loaded.") > loaded || PreviewProcesses().Length >= 3, 900000), "A previewer runs while Visual Studio is asked to exit");
        return passed;
    }
}
