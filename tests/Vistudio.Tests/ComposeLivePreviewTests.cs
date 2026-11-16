using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core.Preview;

/// <summary>Interactive Compose preview: manifest and protocol checks, and the live host on a copy of the Compose sample.</summary>
internal static class ComposeLivePreviewTests
{
    internal static void Run(Action<bool, string> check)
    {
        var manifest = ComposeLiveManifest.Parse("J\tC:\\jdk\\bin\\java.exe\r\nH\tC:\\p\\build\\vistudio\\preview-renderer\nP\tC:\\g\\ui-desktop-1.12.2.jar\nP\tC:\\g\\skiko-awt.jar\n"
            + "R\tC:\\p\\app\\build\\classes\\kotlin\\desktop\\main\nR\tC:\\p\\core\\build\\libs\\core-desktop.jar\nM\t:composeApp\tdesktop\njunk\nP\t\n");
        check(manifest.JavaExecutable == @"C:\jdk\bin\java.exe" && manifest.HostClasses == @"C:\p\build\vistudio\preview-renderer"
            && manifest.Libraries.SequenceEqual(new[] { @"C:\g\ui-desktop-1.12.2.jar", @"C:\g\skiko-awt.jar" })
            && manifest.Reloadable.Count == 2 && manifest.Module == ":composeApp" && manifest.Target == "desktop", "Live manifest lines");
        check(manifest.CreateArgumentFile(@"C:\w\host") ==
            "-Dskiko.renderApi=SOFTWARE\n-cp\n\"C:\\\\w\\\\host;C:\\\\g\\\\ui-desktop-1.12.2.jar;C:\\\\g\\\\skiko-awt.jar\"\nVistudioLivePreviewHost\n",
            "Java argument file: quoted class path with doubled backslashes");
        var changed = ComposeLiveManifest.Parse("J\tC:\\jdk\\bin\\java.exe\nH\th\nP\tC:\\g\\ui-desktop-1.12.3.jar\nR\tC:\\p\\other");
        var sameLibraries = ComposeLiveManifest.Parse("J\tC:\\jdk\\bin\\java.exe\nH\th2\nP\tC:\\g\\ui-desktop-1.12.2.jar\nP\tC:\\g\\skiko-awt.jar\nR\tC:\\p\\other");
        check(manifest.HostKey != changed.HostKey && manifest.HostKey == sameLibraries.HostKey, "A host is reused while JVM and libraries are the same");
        var rejected = false;
        try { ComposeLiveManifest.Parse("P\tC:\\g\\a.jar\n"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "A manifest without JVM and host classes is rejected");

        check(ComposeLiveProtocol.Show("sample.ui.InteractiveKt", "CounterPreview", 240, 0, 1.5) == "show\tsample.ui.InteractiveKt\tCounterPreview\t240\t0\t1.5",
            "show command");
        check(ComposeLiveProtocol.Pointer(ComposePointerKind.Press, 12.25, 40, ComposeLiveProtocol.ButtonPrimary, ComposeLiveProtocol.ModifierControl, 0)
            == "pointer\tpress\t12.25\t40\t1\t2\t0\t0\t0", "pointer press command");
        check(ComposeLiveProtocol.Pointer(ComposePointerKind.Scroll, 1, 2, 0, 0, scrollY: -1.5) == "pointer\tscroll\t1\t2\t0\t0\t-1\t0\t-1.5", "pointer scroll command");
        check(ComposeLiveProtocol.Key(ComposeKeyKind.Typed, 0x41, 'a', 0) == "key\ttyped\t0\t97\t0\t0", "typed characters carry no key code");
        check(ComposeLiveProtocol.Key(ComposeKeyKind.Press, 0x0D, 0, 0) == "key\tpress\t10\t10\t0\t1", "Enter: AWT key code and character 10");
        check(ComposeLiveProtocol.Key(ComposeKeyKind.Release, 0xA1, 0, ComposeLiveProtocol.ModifierShift) == "key\trelease\t16\t0\t1\t3", "Right Shift: key 16, right location");
        check(ComposeLiveProtocol.AwtKeyCode(0x2E) == 127 && ComposeLiveProtocol.AwtKeyCode(0x2D) == 155 && ComposeLiveProtocol.AwtKeyCode(0x25) == 37
            && ComposeLiveProtocol.AwtKeyCode(0x41) == 65 && ComposeLiveProtocol.AwtKeyCode(0xBF) == 47 && ComposeLiveProtocol.AwtKeyCode(0x7C) == 0xF000
            && ComposeLiveProtocol.AwtKeyLocation(0x65) == 4, "Windows virtual keys map to AWT key codes");
        const int control = ComposeLiveProtocol.ModifierControl, shift = ComposeLiveProtocol.ModifierShift, alt = ComposeLiveProtocol.ModifierAlt;
        check(ComposeLiveProtocol.GoesToPreview(0x41, 0) && ComposeLiveProtocol.GoesToPreview(0x08, 0) && ComposeLiveProtocol.GoesToPreview(0x09, shift)
            && ComposeLiveProtocol.GoesToPreview(0x0D, 0) && ComposeLiveProtocol.GoesToPreview(0x25, control | shift) && ComposeLiveProtocol.GoesToPreview(0x56, control),
            "Typing, navigation and editing chords go to the focused preview");
        check(!ComposeLiveProtocol.GoesToPreview(0x53, control) && !ComposeLiveProtocol.GoesToPreview(0x74, 0) && !ComposeLiveProtocol.GoesToPreview(0x1B, 0)
            && !ComposeLiveProtocol.GoesToPreview(0x46, alt) && !ComposeLiveProtocol.GoesToPreview(0x12, 0) && !ComposeLiveProtocol.GoesToPreview(0x09, control),
            "Ctrl+S, F5, Escape, Alt chords and Ctrl+Tab stay with the IDE");
        var tab = false;
        try { ComposeLiveProtocol.Load("C:\\a\tb"); } catch (ArgumentException) { tab = true; }
        check(tab, "Fields with tabs are rejected");

        var text = ComposeLiveProtocol.ParseText(Encoding.UTF8.GetBytes("error\tshow\tjava.lang.IllegalStateException: boom\\nat a.B(C.kt:1)\\tx\\\\y"));
        check(text.Length == 3 && text[2] == "java.lang.IllegalStateException: boom\nat a.B(C.kt:1)\tx\\y", "Text message fields are unescaped");

        var stream = new MemoryStream();
        void Message(char type, byte[] payload)
        {
            stream.WriteByte((byte)type);
            stream.Write(new[] { (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length }, 0, 4);
            stream.Write(payload, 0, payload.Length);
        }
        byte[] Int(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        var pixels = new byte[] { 1, 2, 3, 255, 4, 5, 6, 128 };
        var frameBytes = Int(7).Concat(Int(2)).Concat(Int(1)).Concat(Int(BitConverter.ToInt32(BitConverter.GetBytes(1.5f), 0))).Concat(Int(0)).Concat(pixels).ToArray();
        Message('T', Encoding.UTF8.GetBytes("ready\t11.0.31"));
        Message('F', frameBytes);
        stream.Position = 0;
        var first = ComposeLiveProtocol.ReadMessageAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        var second = ComposeLiveProtocol.ReadMessageAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        var end = ComposeLiveProtocol.ReadMessageAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        check(first?.Key == 'T' && ComposeLiveProtocol.ParseText(first.Value.Value)[0] == "ready" && second?.Key == 'F' && end == null, "Messages are read to the end of the stream");
        var frame = ComposeLiveProtocol.ParseFrame(second!.Value.Value);
        check(frame.Number == 7 && frame.Width == 2 && frame.Height == 1 && frame.Scale == 1.5 && frame.Pixel(0, 0) == 0xFF030201u && frame.Pixel(1, 0) == 0x80060504u,
            "Frame header and BGRA pixels");
        var truncated = false;
        try { ComposeLiveProtocol.ParseFrame(frameBytes.Take(frameBytes.Length - 1).ToArray()); } catch (InvalidDataException) { truncated = true; }
        check(truncated, "A frame whose pixels do not match its size is rejected");
    }

    private const uint Red = 0xFFD32F2F, Green = 0xFF2E7D32, Blue = 0xFF1565C0, Purple = 0xFF6A1B9A;

    /// <summary>
    /// The live preview on a copy of samples/compose-multiplatform: real Gradle wrapper, the shipped init script, renderer
    /// and host. Clicks, typing, an effect that runs by itself, wrap-content size, a failing preview, reload after an
    /// edit, a compile error, a module without Compose, and what is left after the session ends.
    /// </summary>
    internal static async Task<int> RunLiveAsync(string sampleRoot, string javaHome, string buildDirectory)
    {
        var failures = new List<string>();
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            Console.WriteLine((condition ? "ok   " : "FAIL ") + message);
            if (!condition) failures.Add(message);
        }


        var copy = Path.Combine(Path.GetTempPath(), "Vistudio live " + Guid.NewGuid().ToString("N").Substring(0, 8));
        CopySample(sampleRoot, copy);
        var ui = Path.Combine(copy, "composeApp", "src", "commonMain", "kotlin", "sample", "ui");
        var interactive = Path.Combine(ui, "Interactive.kt");
        var cards = Path.Combine(ui, "Cards.kt");
        var session = new ComposeLiveSession(copy, javaHome, Path.Combine(buildDirectory, "ComposePreview.gradle"), Path.Combine(buildDirectory, "VistudioPreviewRenderer.java"));
        var work = session.WorkDirectory;
        var sync = new object();
        ComposeLiveFrame? latest = null;
        var frames = 0;
        var unsolicited = new List<string>();
        session.Frame += frame =>
        {
            lock (sync) { latest = frame; frames++; }
            session.Acknowledge(frame);
        };
        session.Failed += (where, message) => { lock (sync) unsolicited.Add(where + ": " + message); };
        session.Log += line => Console.WriteLine("host: " + line);
        var gradle = new List<string>();
        var ids = new List<int>();

        async Task<ComposeLiveFrame?> WaitAsync(Func<ComposeLiveFrame, bool> accept, int milliseconds = 10000)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                ComposeLiveFrame? frame;
                lock (sync) frame = latest;
                if (frame != null && accept(frame)) return frame;
                await Task.Delay(20);
            }
            return null;
        }
        void Click(double x, double y)
        {
            session.SendPointer(ComposePointerKind.Move, x, y, 0, 0);
            session.SendPointer(ComposePointerKind.Press, x, y, ComposeLiveProtocol.ButtonPrimary, 0, 0);
            session.SendPointer(ComposePointerKind.Release, x, y, 0, 0, 0);
            // Off the clicked element again: under the pointer it is drawn with its hover tint, not its own color.
            session.SendPointer(ComposePointerKind.Move, 1, 1, 0, 0);
        }
        async Task<string?> CompileAsync(string file, string label)
        {
            gradle.Clear();
            var watch = Stopwatch.StartNew();
            var error = await session.CompileAsync(file, line => { lock (gradle) gradle.Add(line); }, CancellationToken.None);
            Console.WriteLine("TIME " + label + ": Gradle " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
            if (error != null) foreach (var line in gradle.Where(l => l.StartsWith("e: ") || l.StartsWith("VISTUDIO_") || l.Contains("FAILED") || l.StartsWith("* What went wrong"))) Console.WriteLine("gradle: " + line);
            return error;
        }
        Task<string?> ShowAsync(ComposePreviewFile file, string function, double scale)
        {
            var preview = file.Renderable.First(p => p.Function == function);
            lock (sync) latest = null;
            return session.ShowAsync(file.JvmClass, preview.Function, preview.WidthDp, preview.HeightDp, scale, CancellationToken.None);
        }

        try
        {
            var previews = ComposePreviewScanner.Scan(interactive, File.ReadAllText(interactive));
            Check(previews.Renderable.Select(p => p.Function).SequenceEqual(new[] { "CounterPreview", "TextFieldPreview", "TimerPreview" }), "Interactive.kt has three renderable previews");

            var error = await CompileAsync(interactive, "first compile");
            Check(error == null, "Gradle compiles the desktop target and the host: " + (error ?? "ok"));
            if (error != null) return Finish();
            var manifest = session.Manifest!;
            Check(manifest.Module == ":composeApp" && manifest.Target == "desktop" && File.Exists(manifest.JavaExecutable), "Manifest names module " + manifest.Module + ", target " + manifest.Target);
            Check(manifest.Libraries.Any(l => Path.GetFileName(l).StartsWith("ui-desktop")) && manifest.Libraries.All(l => !l.StartsWith(copy, StringComparison.OrdinalIgnoreCase)),
                manifest.Libraries.Count + " libraries, none from the project");
            Check(manifest.Reloadable.Count > 0 && manifest.Reloadable.All(r => r.StartsWith(copy, StringComparison.OrdinalIgnoreCase)),
                "Reloadable entries are the project's outputs: " + string.Join(", ", manifest.Reloadable.Select(r => r.Substring(copy.Length))));

            var startWatch = Stopwatch.StartNew();
            error = await session.LoadAsync(CancellationToken.None);
            Check(error == null && session.HostProcessId != null, "The host starts and loads the classes in " + startWatch.Elapsed.TotalSeconds.ToString("0.0") + " s: " + (error ?? "ok"));
            if (error != null)
            {
                if (session.HostProcessId is int stuck) Console.WriteLine(Jstack(javaHome, stuck));
                return Finish();
            }
            if (session.HostProcessId is int hostId) ids.Add(hostId);

            // Counter: a click changes state, the next frame shows it.
            startWatch.Restart();
            error = await ShowAsync(previews, "CounterPreview", 1);
            Check(error == null, "CounterPreview is shown in " + startWatch.ElapsedMilliseconds + " ms: " + (error ?? "ok"));
            var frame = await WaitAsync(f => f.Pixel(40, 35) == Red);
            Check(frame != null && frame.Width == 240 && frame.Height == 120 && frame.Scale == 1.0, "First frame: 240x120 px at scale 1, button red");
            Check(frame != null && frame.Pixel(5, 5) == 0, "Outside the button the frame is transparent (the window draws the background)");
            var clickWatch = Stopwatch.StartNew();
            Click(40, 35);
            frame = await WaitAsync(f => f.Pixel(40, 35) == Green);
            Check(frame != null, "A click turns the button green after " + clickWatch.ElapsedMilliseconds + " ms");
            Click(40, 35);
            frame = await WaitAsync(f => f.Pixel(40, 35) == Blue);
            Check(frame != null, "A second click turns it blue");
            session.SendPointer(ComposePointerKind.Move, 40, 35, 0, 0);
            Check(await WaitAsync(f => f.Pixel(40, 35) != Blue && f.Pixel(40, 35) >> 24 == 0xFF) != null, "The pointer over the button shows its hover tint");
            session.SendPointer(ComposePointerKind.Move, 1, 1, 0, 0);
            Check(await WaitAsync(f => f.Pixel(40, 35) == Blue) != null, "The tint goes when the pointer leaves");
            Click(5, 5);
            await Task.Delay(300);
            lock (sync) frame = latest;
            Check(frame != null && frame.Pixel(40, 35) == Blue, "A click outside the button changes nothing");

            error = await ShowAsync(previews, "CounterPreview", 2);
            frame = await WaitAsync(f => f.Scale == 2.0 && f.Pixel(80, 70) == Red);
            Check(error == null && frame != null && frame.Width == 480 && frame.Height == 240, "At scale 2 the frame is 480x240 px and the state starts over");
            Click(80, 70);
            Check(await WaitAsync(f => f.Pixel(80, 70) == Green) != null, "Pointer positions are frame pixels at scale 2");

            // Text field: focus by click, then typed characters.
            error = await ShowAsync(previews, "TextFieldPreview", 1);
            frame = await WaitAsync(f => f.Height == 120 && BarColor(f) == Red);
            Check(error == null && frame != null, "TextFieldPreview is shown, bar red: " + (error ?? "ok"));
            Click(60, 36);
            foreach (var ch in "abc")
            {
                session.SendKey(ComposeKeyKind.Press, char.ToUpperInvariant(ch), 0, 0);
                session.SendKey(ComposeKeyKind.Typed, 0, ch, 0);
                session.SendKey(ComposeKeyKind.Release, char.ToUpperInvariant(ch), 0, 0);
            }
            Check(await WaitAsync(f => BarColor(f) == Green) != null, "Typing \"abc\" into the focused field turns the bar green");
            session.SendKey(ComposeKeyKind.Press, 0x08, 0, 0);
            session.SendKey(ComposeKeyKind.Release, 0x08, 0, 0);
            Check(await WaitAsync(f => BarColor(f) == Red) != null, "Backspace deletes a character: the bar is red again");

            // Timer: frames keep coming without input.
            error = await ShowAsync(previews, "TimerPreview", 1);
            var seen = new HashSet<uint>();
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 4000 && seen.Count < 3)
            {
                lock (sync) if (latest != null && latest.Width == 120 && latest.Height == 120) seen.Add(latest.Pixel(60, 60));
                await Task.Delay(20);
            }
            Check(error == null && seen.SetEquals(new[] { Red, Green, Blue }), "A LaunchedEffect with delay changes the preview by itself: " + seen.Count + " colors in " + timer.ElapsedMilliseconds + " ms");

            // Wrap content and a preview that throws, from Cards.kt (same module, so no compile is needed).
            var cardFile = ComposePreviewScanner.Scan(cards, File.ReadAllText(cards));
            error = await ShowAsync(cardFile, "GreetingCardPreview", 2);
            frame = await WaitAsync(f => f.Scale == 2.0 && f.Width > 300);
            await Task.Delay(300);
            lock (sync) frame = latest;
            Check(error == null && frame != null && frame.Width == 416 && frame.Height == 207,
                "A preview without a size wraps its content: " + (frame == null ? "no frame" : frame.Width + "x" + frame.Height) + " px at scale 2 (the static render is 416x207)");
            error = await ShowAsync(cardFile, "FailingPreview", 1);
            Check(error != null && error.Contains("This preview fails on purpose") && error.Contains("Cards.kt"), "A preview that throws reports its exception: " + error?.Split('\n')[0]);
            error = await ShowAsync(previews, "CounterPreview", 1);
            Check(error == null && await WaitAsync(f => f.Width == 240 && f.Pixel(40, 35) == Red) != null, "The host shows another preview after a failed one");

            // Reload: edit, compile with the session's daemon, load. The host stays, the preview starts over with the new code.
            Click(40, 35);
            Check(await WaitAsync(f => f.Pixel(40, 35) == Green) != null, "Clicked once before the edit");
            var original = File.ReadAllText(interactive);
            File.WriteAllText(interactive, original.Replace("Color(0xFFD32F2F), Color(0xFF2E7D32)", "Color(0xFF6A1B9A), Color(0xFF2E7D32)"));
            error = await CompileAsync(interactive, "compile after an edit");
            Check(error == null, "The edited file compiles: " + (error ?? "ok"));
            var hostBefore = session.HostProcessId;
            startWatch.Restart();
            error = await session.LoadAsync(CancellationToken.None);
            frame = await WaitAsync(f => f.Pixel(40, 35) == Purple);
            Check(error == null && frame != null && session.HostProcessId == hostBefore,
                "After load the same host shows the new color (" + startWatch.ElapsedMilliseconds + " ms): " + (error ?? "ok"));
            Click(40, 35);
            Check(await WaitAsync(f => f.Pixel(40, 35) == Green) != null, "The reloaded preview reacts to clicks");

            // A compile error keeps the running preview.
            File.WriteAllText(interactive, File.ReadAllText(interactive) + "\nval broken: Int = \"text\"\n");
            error = await CompileAsync(interactive, "compile with an error");
            Check(error != null && error.StartsWith("Compilation failed: ") && error.Contains("Interactive.kt"), "A compiler error is reported: " + error);
            Click(40, 35);
            Check(await WaitAsync(f => f.Pixel(40, 35) == Blue) != null, "The preview from before the error is still interactive");
            File.WriteAllText(interactive, original);

            // A module without Compose.
            error = await CompileAsync(Path.Combine(copy, "core", "src", "commonMain", "kotlin", "sample", "core", "Greeting.kt"), "module without Compose");
            Check(error != null && error.Contains("no Compose UI for desktop"), "A module without Compose is reported: " + error);

            lock (sync) Check(unsolicited.Count == 0, "No errors outside requests" + (unsolicited.Count == 0 ? "" : ": " + string.Join(" | ", unsolicited)));
            ids.AddRange(session.GradleProcessIds());
            Check(session.GradleProcessIds().Length > 0, "The session keeps a Gradle daemon (" + session.GradleProcessIds().Length + " processes) between compiles");
            Console.WriteLine("INFO frames received: " + frames);
            return Finish();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FAIL: " + e);
            failures.Add(e.Message);
            return Finish();
        }

        int Finish()
        {
            session.Dispose();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 15000 && (ids.Any(Alive) || Directory.Exists(work))) Thread.Sleep(200);
            Check(!ids.Any(Alive), "Host and Gradle daemon are gone after the session ends (" + ids.Count + " processes checked)");
            Check(!Directory.Exists(work), "The session's work directory is removed");
            var deleted = false;
            for (var attempt = 0; attempt < 20 && !deleted; attempt++)
            {
                try { Directory.Delete(copy, true); deleted = true; }
                catch (IOException) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { Thread.Sleep(500); }
            }
            Check(deleted, "Nothing keeps files of the project open: the copy can be deleted");
            Console.WriteLine((failures.Count == 0 ? "PASS: " : "FAIL: " + failures.Count + " of ") + checks + " checks (Compose live preview on the sample).");
            return failures.Count == 0 ? 0 : 1;
        }
    }

    /// <summary>The color of TextFieldPreview's bar: the lowest opaque red or green pixel in the middle column.</summary>
    private static uint BarColor(ComposeLiveFrame frame)
    {
        for (var y = frame.Height - 1; y >= 0; y--)
        {
            var pixel = frame.Pixel(frame.Width / 2, y);
            if (pixel == Red || pixel == Green) return pixel;
        }
        return 0;
    }

    private static string Jstack(string javaHome, int pid)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(javaHome, "bin", "jstack.exe"), pid.ToString()) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var process = Process.Start(start)!) return process.StandardOutput.ReadToEnd();
        }
        catch (Exception e) { return "jstack: " + e.Message; }
    }

    private static bool Alive(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static void CopySample(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(directory);
            if (name == "build" || name == ".gradle" || name == ".kotlin" || name == ".vs" || name == ".idea") continue;
            CopySample(directory, Path.Combine(to, name));
        }
    }
}
