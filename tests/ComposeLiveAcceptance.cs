// End-to-end Visual Studio acceptance for the interactive mode of the Kotlin Compose Preview window, driven through
// typed EnvDTE calls and UI Automation (the window's controls carry ComposePreview.* automation ids). What the window
// shows is read from a PrintWindow capture of the IDE. Mouse and keyboard reach the preview only as real input, so
// those two steps move the pointer and type; they are skipped (and reported) when the IDE cannot be brought to the
// foreground. Compiled by verify-compose-live.ps1.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using EnvDTE;
using EnvDTE80;

public sealed class ComposeLiveAcceptance
{
    private readonly DTE2 dte;
    private readonly Action<string> say;
    private int passed;
    private string evidence = Path.GetTempPath();

    public ComposeLiveAcceptance(object dte, Action<string> say)
    {
        this.dte = (DTE2)dte;
        this.say = say;
        // Physical pixels everywhere: UI Automation rectangles, the capture and the pointer position must agree.
        say("SetProcessDPIAware: " + SetProcessDPIAware());
    }

    /// <summary>Real mouse and keyboard steps that could not run because the IDE did not get the foreground.</summary>
    public int Skipped { get; private set; }

    // ---- Checks and evidence ----

    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            say("FAIL: " + message);
            using (var failure = Capture("failure.png")) { }
            say("Status line: " + Status);
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
            catch (COMException) { }
            catch (ElementNotAvailableException) { }
            System.Threading.Thread.Sleep(500);
        }
        try { return condition(); }
        catch (Exception) { return false; }
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>The Visual Studio main window (PW_RENDERFULLCONTENT also captures WPF content), optionally saved as a PNG.</summary>
    private Bitmap Capture(string name = null)
    {
        IntPtr hwnd = dte.MainWindow.HWnd;
        GetWindowRect(hwnd, out var r);
        var bitmap = new Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            PrintWindow(hwnd, hdc, 2);
            graphics.ReleaseHdc(hdc);
        }
        if (name != null)
        {
            try
            {
                var path = Path.Combine(evidence, name);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                say("Screenshot: " + path);
            }
            catch (Exception e) { say("Screenshot failed: " + e.Message); }
        }
        return bitmap;
    }

    /// <summary>The color the window shows at a position inside the running preview, given as fractions of its size.</summary>
    private Color LivePixel(double fx, double fy)
    {
        var image = ById("ComposePreview.LiveImage");
        if (image == null) return Color.Empty;
        var bounds = image.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width < 4) return Color.Empty;
        GetWindowRect(dte.MainWindow.HWnd, out var r);
        using (var bitmap = Capture())
        {
            int x = (int)(bounds.Left + bounds.Width * fx) - r.Left, y = (int)(bounds.Top + bounds.Height * fy) - r.Top;
            return x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height ? Color.Empty : bitmap.GetPixel(x, y);
        }
    }

    private static bool Near(Color color, int rgb, int tolerance = 14) =>
        color.A != 0 && Math.Abs(color.R - ((rgb >> 16) & 0xFF)) <= tolerance && Math.Abs(color.G - ((rgb >> 8) & 0xFF)) <= tolerance && Math.Abs(color.B - (rgb & 0xFF)) <= tolerance;

    private static string Hex(Color color) => color.IsEmpty ? "none" : color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    // ---- DTE helpers ----

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

    /// <summary>The preview host JVMs this Visual Studio started (their work directory carries its process id).</summary>
    private string[] Hosts()
    {
        var mine = "s-" + System.Diagnostics.Process.GetProcessById(ProcessIdOf(dte)).Id + "-";
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'java.exe' AND CommandLine LIKE '%compose-live%'"))
            return searcher.Get().Cast<ManagementObject>().Where(o => ((string)o["CommandLine"] ?? "").Contains(mine)).Select(o => o["ProcessId"].ToString()).ToArray();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    private static int ProcessIdOf(DTE2 dte) { GetWindowThreadProcessId(dte.MainWindow.HWnd, out var id); return id; }

    /// <summary>Session folders (host files, Gradle daemon registry) of this Visual Studio under %LOCALAPPDATA%\Vistudio\compose-live.</summary>
    private string[] SessionFolders()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "compose-live");
        return Directory.Exists(parent) ? Directory.GetDirectories(parent, "s-" + ProcessIdOf(dte) + "-*", SearchOption.AllDirectories) : new string[0];
    }

    // ---- UI Automation helpers ----

    private AutomationElement Main() => AutomationElement.FromHandle(dte.MainWindow.HWnd);

    private AutomationElement ById(string id) =>
        Main().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private string Status => ById("ComposePreview.Status")?.Current.Name ?? "";

    private void Invoke(string id)
    {
        var element = ById(id) ?? throw new Exception("No UI element " + id);
        say("> invoke " + id);
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private string ErrorText()
    {
        var element = ById("ComposePreview.LiveError");
        if (element == null) return "";
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : element.Current.Name;
    }

    // ---- Real input ----

    private bool Foreground()
    {
        IntPtr hwnd = dte.MainWindow.HWnd;
        for (var attempt = 0; attempt < 3 && GetForegroundWindow() != hwnd; attempt++)
        {
            // A process that does not own the foreground may only set it after input of its own: an Alt tap is that input.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            try { dte.MainWindow.Activate(); } catch (COMException) { }
            System.Threading.Thread.Sleep(700);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Moves the pointer into the running preview and clicks. False when the IDE is not in the foreground.</summary>
    private bool ClickLive(double fx, double fy)
    {
        var image = ById("ComposePreview.LiveImage") ?? throw new Exception("No running preview image.");
        var bounds = image.Current.BoundingRectangle;
        if (!Foreground()) return false;
        int x = (int)(bounds.Left + bounds.Width * fx), y = (int)(bounds.Top + bounds.Height * fy);
        say("> click at " + x + "," + y + " (preview " + (int)bounds.Width + "x" + (int)bounds.Height + " px on screen)");
        SetCursorPos(x - 3, y - 3);
        mouse_event(1, 1, 1, 0, UIntPtr.Zero); // a small move first: the preview learns where the pointer is
        System.Threading.Thread.Sleep(150);
        SetCursorPos(x, y);
        mouse_event(1, 1, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(150);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(300);
        return true;
    }

    private void Type(string text)
    {
        foreach (var ch in text.ToUpperInvariant())
        {
            keybd_event((byte)ch, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            keybd_event((byte)ch, 0, 2, UIntPtr.Zero);
            System.Threading.Thread.Sleep(120);
        }
    }

    private void StartInteractive(string function, string id)
    {
        Check(Wait(() => ById("ComposePreview.Interactive." + function + "." + id) != null, 60000), "The list offers Interactive for " + function);
        Invoke("ComposePreview.Interactive." + function + "." + id);
        Check(Wait(() => Status.Contains("interactive since"), 900000), function + " runs interactively: " + Status);
    }

    // ---- The run ----

    public int Run(string sampleRoot, string evidenceFolder)
    {
        evidence = evidenceFolder;
        try { dte.MainWindow.WindowState = vsWindowState.vsWindowStateMaximize; } catch (Exception) { }
        var interactive = Path.Combine(sampleRoot, "composeApp", "src", "commonMain", "kotlin", "sample", "ui", "Interactive.kt");
        const int red = 0xD32F2F, green = 0x2E7D32, purple = 0x6A1B9A;

        // The static list first: it is where the interactive mode starts from.
        var document = Open(interactive);
        var original = Text(document);
        say("> Kotlin.ComposePreview");
        dte.ExecuteCommand("Kotlin.ComposePreview", "");
        Check(Wait(() => ById("ComposePreview.Status") != null, 60000), "The Kotlin Compose Preview window is open");
        // The command can arrive before Visual Studio has finished opening the folder; the window then renders by itself
        // once the workspace is known. A Refresh here would hide a window that does not.
        Check(Wait(() => Status.Contains(" rendered"), 1200000), "The previews of Interactive.kt are rendered as images without a Refresh: " + Status);

        // Counter: frames reach the window.
        StartInteractive("CounterPreview", "p0");
        Check(Wait(() => Hosts().Length == 1, 15000), "One preview host JVM runs for this Visual Studio (" + string.Join(", ", Hosts()) + ")");
        var host = Hosts().FirstOrDefault();
        Check(Wait(() => Near(LivePixel(40 / 240.0, 35 / 120.0), red), 30000), "The window shows the running counter: its button is red (" + Hex(LivePixel(40 / 240.0, 35 / 120.0)) + ")");
        using (Capture("live-counter.png")) { }

        // Real mouse and keyboard.
        if (ClickLive(40 / 240.0, 35 / 120.0))
        {
            // Under the pointer the button is drawn with its hover tint, so green is checked loosely.
            Check(Wait(() => { var c = LivePixel(40 / 240.0, 35 / 120.0); return Near(c, green, 30) && c.G > c.R + 40; }, 15000),
                "A mouse click in the window reaches the preview: the button is green (" + Hex(LivePixel(40 / 240.0, 35 / 120.0)) + ")");
            using (Capture("live-counter-clicked.png")) { }

            Invoke("ComposePreview.Back");
            Check(Wait(() => Status.Contains(" rendered") && Hosts().Length == 0, 600000), "Back returns to the list and ends the host: " + Status);
            StartInteractive("TextFieldPreview", "p1");
            Check(Wait(() => Near(LivePixel(0.5, 80 / 120.0), red), 30000), "The text field preview shows its red bar (" + Hex(LivePixel(0.5, 80 / 120.0)) + ")");
            if (ClickLive(60 / 240.0, 36 / 120.0))
            {
                Type("abc");
                Check(Wait(() => Near(LivePixel(0.5, 80 / 120.0), green), 15000), "Typing abc goes to the focused preview: the bar is green (" + Hex(LivePixel(0.5, 80 / 120.0)) + ")");
                Check(Text(document) == original, "The keys did not reach the editor: Interactive.kt is unchanged");
                using (Capture("live-textfield-typed.png")) { }
            }
            else { Skipped++; say("SKIP: typing into the preview (Visual Studio did not get the foreground)"); }
            Invoke("ComposePreview.Back");
            Check(Wait(() => Status.Contains(" rendered") && Hosts().Length == 0, 600000), "Back from the text field preview: " + Status);
            StartInteractive("CounterPreview", "p0");
            host = Hosts().FirstOrDefault();
            // Park the pointer away from the preview: no hover tint in the color checks below.
            SetCursorPos(5, 5);
            mouse_event(1, 1, 1, 0, UIntPtr.Zero);
        }
        else { Skipped += 2; say("SKIP: clicking and typing in the preview (Visual Studio did not get the foreground)"); }

        // Reload on save: the same host shows the edited code.
        document = Open(interactive);
        Replace(document, "Color(0xFFD32F2F), Color(0xFF2E7D32)", "Color(0xFF6A1B9A), Color(0xFF2E7D32)");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        document.Save();
        Check(Wait(() => Near(LivePixel(40 / 240.0, 35 / 120.0), purple), 900000),
            "Saving an edit reloads the running preview: the button is purple after " + (int)watch.Elapsed.TotalSeconds + " s (" + Hex(LivePixel(40 / 240.0, 35 / 120.0)) + ")");
        Check(Hosts().FirstOrDefault() == host, "The reload kept the host JVM (process " + host + ")");
        using (Capture("live-counter-reloaded.png")) { }

        // A compiler error keeps the running preview and is shown above it.
        var end = ((TextDocument)document.Object("TextDocument")).EndPoint.CreateEditPoint();
        end.Insert("\nval broken: Int = \"text\"\n");
        document.Save();
        Check(Wait(() => Status.Contains("Compilation failed"), 900000), "A compiler error is reported in the status line: " + Status);
        Check(ErrorText().Contains("Initializer type mismatch"), "The error is shown above the preview: " + ErrorText().Split('\n')[0]);
        Check(Near(LivePixel(40 / 240.0, 35 / 120.0), purple) && Hosts().FirstOrDefault() == host, "The preview from before the error still runs");
        Replace(document, "\nval broken: Int = \"text\"\n", "\n");
        document.Save();
        Check(Wait(() => Status.Contains("interactive since"), 900000), "After the fix the preview is interactive again: " + Status);

        // Back ends the host and the Gradle daemon, and removes the session's files.
        Invoke("ComposePreview.Back");
        Check(Wait(() => Hosts().Length == 0 && SessionFolders().Length == 0, 60000),
            "Back ends the host and the daemon: no host process, no session folder (" + Hosts().Length + " hosts, " + SessionFolders().Length + " folders)");
        Check(Wait(() => Status.Contains(" rendered") || Status.Contains("Rendering"), 60000), "The list of previews is back: " + Status);
        Wait(() => Status.Contains(" rendered"), 900000);

        // Restore the file, then leave a preview running: the script quits Visual Studio and checks that nothing is left.
        document = Open(interactive);
        Replace(document, "Color(0xFF6A1B9A), Color(0xFF2E7D32)", "Color(0xFFD32F2F), Color(0xFF2E7D32)");
        document.Save();
        Wait(() => Status.Contains(" rendered"), 900000);
        StartInteractive("CounterPreview", "p0");
        Check(Wait(() => Hosts().Length == 1, 15000), "A host runs while Visual Studio is asked to exit");
        return passed;
    }
}
