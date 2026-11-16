using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Preview
{
    /// <summary>What ComposePreview.gradle's live task wrote: how to start the interactive preview host of one JVM target.</summary>
    public sealed class ComposeLiveManifest
    {
        private ComposeLiveManifest(string java, string hostClasses, IReadOnlyList<string> libraries, IReadOnlyList<string> reloadable, string? module, string? target)
        { JavaExecutable = java; HostClasses = hostClasses; Libraries = libraries; Reloadable = reloadable; Module = module; Target = target; }

        public string JavaExecutable { get; }
        /// <summary>The compiled VistudioLivePreviewHost and VistudioPreviewRenderer.</summary>
        public string HostClasses { get; }
        /// <summary>The host JVM's class path: the libraries of the target from the Gradle user home.</summary>
        public IReadOnlyList<string> Libraries { get; }
        /// <summary>Class directories and jars built in the project; the host loads copies and replaces them on reload.</summary>
        public IReadOnlyList<string> Reloadable { get; }
        public string? Module { get; }
        public string? Target { get; }

        /// <summary>A running host can take a new manifest with "load" only while this is unchanged; otherwise it restarts.</summary>
        public string HostKey => JavaExecutable + "\n" + string.Join("\n", Libraries);

        public static ComposeLiveManifest Parse(string text)
        {
            string? java = null, host = null, module = null, target = null;
            var libraries = new List<string>();
            var reloadable = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var field = raw.TrimEnd('\r').Split('\t');
                if (field.Length < 2 || field[1].Length == 0) continue;
                switch (field[0])
                {
                    case "J": java = field[1]; break;
                    case "H": host = field[1]; break;
                    case "P": libraries.Add(field[1]); break;
                    case "R": reloadable.Add(field[1]); break;
                    case "M": module = field[1]; target = field.Length > 2 ? field[2] : null; break;
                }
            }
            if (java == null || host == null) throw new InvalidDataException("The live preview manifest names no Java executable or host classes.");
            return new ComposeLiveManifest(java, host, libraries, reloadable, module, target);
        }

        /// <summary>
        /// The content of a Java argument file (<c>java @file</c>): the class path of a module easily exceeds the
        /// command line limit. Inside its quotes a backslash escapes, so path separators are doubled.
        /// </summary>
        public string CreateArgumentFile(string hostClasses)
        {
            var classPath = string.Join(";", new[] { hostClasses }.Concat(Libraries));
            if (classPath.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException("A class path entry contains a quote or a line break.");
            // Software rendering: frames are read back from memory, and the host must not depend on a GPU context.
            return "-Dskiko.renderApi=SOFTWARE\n-cp\n\"" + classPath.Replace("\\", "\\\\") + "\"\nVistudioLivePreviewHost\n";
        }
    }

    public enum ComposePointerKind { Move, Press, Release, Scroll, Exit }
    public enum ComposeKeyKind { Press, Release, Typed }

    /// <summary>One rendered frame of the live preview: BGRA premultiplied rows, top down.</summary>
    public sealed class ComposeLiveFrame
    {
        internal ComposeLiveFrame(int number, int width, int height, double scale, byte[] buffer, int offset)
        { Number = number; Width = width; Height = height; Scale = scale; Buffer = buffer; Offset = offset; }
        public int Number { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Pixels per dp.</summary>
        public double Scale { get; }
        /// <summary>The pixels start at <see cref="Offset"/>; a row is <see cref="Width"/> * 4 bytes.</summary>
        public byte[] Buffer { get; }
        public int Offset { get; }

        /// <summary>One pixel as 0xAARRGGBB, colors premultiplied by alpha.</summary>
        public uint Pixel(int x, int y)
        {
            var at = Offset + (y * Width + x) * 4;
            return (uint)(Buffer[at] | Buffer[at + 1] << 8 | Buffer[at + 2] << 16 | Buffer[at + 3] << 24);
        }
    }

    /// <summary>The line and message formats of VistudioLivePreviewHost.java.</summary>
    public static class ComposeLiveProtocol
    {
        public const int ButtonPrimary = 1, ButtonSecondary = 2, ButtonTertiary = 4;
        public const int ModifierShift = 1, ModifierControl = 2, ModifierAlt = 4, ModifierMeta = 8;

        public static string Load(string manifest) => "load\t" + Field(manifest);

        public static string Show(string jvmClass, string method, int widthDp, int heightDp, double scale) =>
            "show\t" + Field(jvmClass) + "\t" + Field(method) + "\t" + Number(widthDp) + "\t" + Number(heightDp) + "\t" + Number(scale);

        /// <param name="x">Pixels of the frame, from its left edge.</param>
        /// <param name="button">The button that went down or up (0 primary, 1 secondary, 2 tertiary), otherwise -1.</param>
        public static string Pointer(ComposePointerKind kind, double x, double y, int buttons, int modifiers, int button = -1, double scrollX = 0, double scrollY = 0) =>
            "pointer\t" + kind.ToString().ToLowerInvariant() + "\t" + Number(x) + "\t" + Number(y) + "\t" + Number(buttons) + "\t" + Number(modifiers)
            + "\t" + Number(button) + "\t" + Number(scrollX) + "\t" + Number(scrollY);

        /// <param name="windowsVirtualKey">Ignored for <see cref="ComposeKeyKind.Typed"/>.</param>
        /// <param name="character">UTF-16 code unit; 0 for keys that type nothing.</param>
        public static string Key(ComposeKeyKind kind, int windowsVirtualKey, int character, int modifiers)
        {
            var typed = kind == ComposeKeyKind.Typed;
            if (!typed && character == 0) character = ControlCharacter(windowsVirtualKey);
            return "key\t" + kind.ToString().ToLowerInvariant() + "\t" + Number(typed ? 0 : AwtKeyCode(windowsVirtualKey)) + "\t" + Number(character)
                + "\t" + Number(modifiers) + "\t" + Number(typed ? 0 : AwtKeyLocation(windowsVirtualKey));
        }

        public static string Ack(int frame) => "ack\t" + Number(frame);
        public const string Frame = "frame";
        public const string Quit = "quit";

        /// <summary>java.awt.event.KeyEvent's key code for a Windows virtual key. Most are equal; these are not.</summary>
        public static int AwtKeyCode(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0x0D: return 10;    // Enter
                case 0x2C: return 154;   // Print Screen
                case 0x2D: return 155;   // Insert
                case 0x2E: return 127;   // Delete
                case 0x5B: case 0x5C: return 524; // Windows
                case 0x5D: return 525;   // Context menu
                case 0xA0: case 0xA1: return 16;  // Shift
                case 0xA2: case 0xA3: return 17;  // Ctrl
                case 0xA4: case 0xA5: return 18;  // Alt
                case 0xBA: return 59;    // ;
                case 0xBB: return 61;    // =
                case 0xBC: return 44;    // ,
                case 0xBD: return 45;    // -
                case 0xBE: return 46;    // .
                case 0xBF: return 47;    // /
                case 0xC0: return 192;   // `
                case 0xDB: return 91;    // [
                case 0xDC: return 92;    // \
                case 0xDD: return 93;    // ]
                case 0xDE: return 222;   // '
            }
            if (virtualKey >= 0x7C && virtualKey <= 0x87) return 0xF000 + (virtualKey - 0x7C); // F13-F24
            return virtualKey;
        }

        /// <summary>
        /// Whether a key pressed while the preview has the keyboard goes to the preview instead of the IDE. The preview
        /// gets typing, navigation and the usual editing chords; the IDE keeps Alt (menus), function keys, Escape
        /// (back to the editor) and every other Ctrl shortcut.
        /// </summary>
        public static bool GoesToPreview(int virtualKey, int modifiers)
        {
            if ((modifiers & ModifierAlt) != 0 || virtualKey == 0x12 || virtualKey == 0xA4 || virtualKey == 0xA5) return false;
            if (virtualKey == 0x1B || virtualKey == 0x5B || virtualKey == 0x5C || virtualKey >= 0x70 && virtualKey <= 0x87) return false;
            if ((modifiers & ModifierControl) == 0) return true;
            switch (virtualKey)
            {
                case 0x11: case 0xA2: case 0xA3:                           // Ctrl itself
                case 0x10: case 0xA0: case 0xA1:                           // Shift, for Ctrl+Shift+arrows
                case 0x41: case 0x43: case 0x56: case 0x58: case 0x59: case 0x5A: // A C V X Y Z
                case 0x25: case 0x26: case 0x27: case 0x28:                // arrows
                case 0x23: case 0x24: case 0x08: case 0x2E:                // End, Home, Backspace, Delete
                    return true;
                default: return false;
            }
        }

        /// <summary>KeyEvent.KEY_LOCATION_*: 1 standard, 2 left, 3 right, 4 number pad.</summary>
        public static int AwtKeyLocation(int virtualKey)
        {
            if (virtualKey == 0xA0 || virtualKey == 0xA2 || virtualKey == 0xA4 || virtualKey == 0x5B) return 2;
            if (virtualKey == 0xA1 || virtualKey == 0xA3 || virtualKey == 0xA5 || virtualKey == 0x5C) return 3;
            return virtualKey >= 0x60 && virtualKey <= 0x6F ? 4 : 1;
        }

        private static int ControlCharacter(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0x08: return 8;
                case 0x09: return 9;
                case 0x0D: return 10;
                case 0x1B: return 27;
                case 0x2E: return 127;
                default: return 0;
            }
        }

        /// <summary>The fields of a 'T' message.</summary>
        public static string[] ParseText(byte[] payload) =>
            Encoding.UTF8.GetString(payload).Split('\t').Select(value =>
                Regex.Replace(value, @"\\([\\tn])", m => m.Groups[1].Value == "t" ? "\t" : m.Groups[1].Value == "n" ? "\n" : "\\")).ToArray();

        /// <summary>An 'F' message: number, width, height, scale (float bits), format, pixels; integers are big-endian.</summary>
        public static ComposeLiveFrame ParseFrame(byte[] payload)
        {
            if (payload.Length < 20) throw new InvalidDataException("The preview frame is truncated.");
            int number = Int(payload, 0), width = Int(payload, 4), height = Int(payload, 8), format = Int(payload, 16);
            var scale = BitConverter.ToSingle(BitConverter.GetBytes(Int(payload, 12)), 0);
            if (format != 0) throw new InvalidDataException("Unknown preview frame format " + format + ".");
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || payload.Length - 20 != (long)width * height * 4 || !(scale > 0))
                throw new InvalidDataException("The preview frame has an invalid size.");
            return new ComposeLiveFrame(number, width, height, scale, payload, 20);
        }

        /// <summary>Reads one message; null at the end of the stream.</summary>
        public static async Task<KeyValuePair<char, byte[]>?> ReadMessageAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[5];
            if (!await FillAsync(stream, header, token).ConfigureAwait(false)) return null;
            var length = Int(header, 1);
            if (length < 0 || length > 1 << 30) throw new InvalidDataException("The preview host sent a message of an invalid length.");
            var payload = new byte[length];
            if (!await FillAsync(stream, payload, token).ConfigureAwait(false)) return null;
            return new KeyValuePair<char, byte[]>((char)header[0], payload);
        }

        private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer, read, buffer.Length - read, token).ConfigureAwait(false);
                if (count <= 0) return false;
                read += count;
            }
            return true;
        }

        private static int Int(byte[] data, int at) => data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Field(string value) =>
            value.IndexOfAny(new[] { '\t', '\r', '\n' }) < 0 ? value : throw new ArgumentException("A preview command field contains a tab or a line break.");
    }

    /// <summary>
    /// One interactive Compose preview: Gradle compiles the JVM target of the previewed file (with a daemon kept for the
    /// session, so a save is rendered again within seconds), and a host JVM composes the preview offscreen, sends frames
    /// and takes pointer and key input. The session owns both; disposing it ends them and removes its files.
    /// </summary>
    public sealed class ComposeLiveSession : IDisposable
    {
        private readonly ProcessJob gradleJob = new ProcessJob();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();
        private readonly string gradleRoot, javaHome, script, renderer;
        private OwnedProcess? host;
        private string? hostKey;
        private ComposeLiveManifest? manifest;
        private Pending? pending;
        // The last "show": the host shows it again by itself after "load"; a host that was started again is told.
        private string? shownCommand;
        private bool hostHasPreview;
        private bool disposed;

        private sealed class Pending
        {
            public Pending(bool load, bool thenShown) { Load = load; ThenShown = thenShown; }
            public bool Load { get; }
            /// <summary>A load of a host that already shows a preview is answered by "loaded", then "shown" or an error.</summary>
            public bool ThenShown { get; }
            public bool Loaded { get; set; }
            public TaskCompletionSource<string?> Result { get; } = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <param name="script">Build\ComposePreview.gradle.</param>
        /// <param name="renderer">Build\VistudioPreviewRenderer.java; the host source is next to it.</param>
        public ComposeLiveSession(string gradleRoot, string javaHome, string script, string renderer)
        {
            this.gradleRoot = Path.GetFullPath(gradleRoot);
            this.javaHome = javaHome ?? "";
            this.script = script;
            this.renderer = renderer;
            var parent = WorkDirectoryFor(this.gradleRoot);
            RemoveStale(parent);
            WorkDirectory = Path.Combine(parent, "s-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(WorkDirectory);
        }

        /// <summary>Manifest, argument file, the host's class copies and the daemon registry. Never inside the project.</summary>
        public string WorkDirectory { get; }
        public ComposeLiveManifest? Manifest => manifest;
        public int? HostProcessId { get { lock (sync) return host?.Id; } }
        /// <summary>The Gradle daemon (and what it started) kept for this session.</summary>
        public int[] GradleProcessIds() => disposed ? Array.Empty<int>() : gradleJob.ProcessIds();

        /// <summary>A frame arrived (on a thread pool thread). Call <see cref="Acknowledge"/> once it is taken.</summary>
        public event Action<ComposeLiveFrame>? Frame;
        /// <summary>The preview failed outside a request: where (render, input, effect, protocol) and the message. The host closed the preview.</summary>
        public event Action<string, string>? Failed;
        /// <summary>What the preview code and the JVM print.</summary>
        public event Action<string>? Log;
        /// <summary>The host process ended on its own.</summary>
        public event Action<int>? HostExited;

        /// <summary><c>%LOCALAPPDATA%\Vistudio\compose-live\&lt;root name&gt;-&lt;hash&gt;</c>.</summary>
        public static string WorkDirectoryFor(string gradleRoot)
        {
            var full = Path.GetFullPath(gradleRoot).TrimEnd('\\', '/');
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                var name = new string(Path.GetFileName(full).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                return SettingsXml.PathFor(Path.Combine("compose-live", (name.Length == 0 ? "root" : name) + "-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2")))));
            }
        }

        /// <summary>Session folders left by an instance that was killed: their process is gone.</summary>
        private static void RemoveStale(string parent)
        {
            if (!Directory.Exists(parent)) return;
            foreach (var directory in Directory.GetDirectories(parent, "s-*"))
            {
                var match = Regex.Match(Path.GetFileName(directory), @"\As-(\d+)-");
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                try { using (Process.GetProcessById(pid)) continue; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                TryDelete(directory);
            }
        }

        /// <summary>
        /// Runs Gradle. Returns why the preview cannot be built (no JVM target, a compiler error, a Gradle failure), or
        /// null: then <see cref="LoadAsync"/> hands the new classes to the host.
        /// </summary>
        public async Task<string?> CompileAsync(string sourceFile, Action<string> onGradleLine, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var file = Path.Combine(WorkDirectory, "manifest.tsv");
                File.Delete(file);
                var start = GradleCommand.CreateLivePreview(gradleRoot, javaHome, script, renderer, sourceFile, file, Path.Combine(WorkDirectory, "daemon"));
                string? scriptError = null, compileError = null;
                Action<string> accept = line =>
                {
                    onGradleLine(line);
                    scriptError ??= ComposePreviewResult.GradleError(line);
                    if (compileError == null && line.StartsWith("e: ", StringComparison.Ordinal)) compileError = line.Substring(3);
                };
                var code = await OwnedProcess.RunAsync(start, accept, accept, token, null, gradleJob).ConfigureAwait(false);
                if (code != 0)
                    return scriptError ?? (compileError != null ? "Compilation failed: " + compileError : "Gradle failed (exit " + code + "). See Output > Kotlin for details.");
                if (!File.Exists(file)) return "Gradle did not write the live preview manifest (" + file + ").";
                manifest = ComposeLiveManifest.Parse(File.ReadAllText(file, Encoding.UTF8));
                return null;
            }
            finally { gate.Release(); }
        }

        /// <summary>
        /// Gives the compiled classes to the host, starting it first when needed (or again when the libraries changed).
        /// A preview that was shown before is shown again from its initial state. Returns the error, or null.
        /// </summary>
        public async Task<string?> LoadAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = manifest ?? throw new InvalidOperationException("Compile before loading.");
                OwnedProcess? running;
                lock (sync) running = host;
                if (running != null && (hostKey != current.HostKey || running.Exit.IsCompleted))
                {
                    await StopHostAsync().ConfigureAwait(false);
                    running = null;
                }
                if (running == null)
                {
                    var failure = await StartHostAsync(current, token).ConfigureAwait(false);
                    if (failure != null) return failure;
                }
                var error = await RequestAsync(ComposeLiveProtocol.Load(Path.Combine(WorkDirectory, "manifest.tsv")), new Pending(load: true, thenShown: hostHasPreview), token)
                    .ConfigureAwait(false);
                if (error != null || hostHasPreview || shownCommand == null) return error;
                hostHasPreview = true;
                return await RequestAsync(shownCommand, new Pending(load: false, thenShown: false), token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        /// <summary>Shows one preview function. A size of 0 wraps the content. Returns the error (for example the exception the preview threw), or null.</summary>
        public async Task<string?> ShowAsync(string jvmClass, string method, int widthDp, int heightDp, double scale, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (host == null) return "The preview host is not running.";
                shownCommand = ComposeLiveProtocol.Show(jvmClass, method, widthDp, heightDp, scale);
                hostHasPreview = true;
                return await RequestAsync(shownCommand, new Pending(load: false, thenShown: false), token)
                    .ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        public void SendPointer(ComposePointerKind kind, double x, double y, int buttons, int modifiers, int button = -1, double scrollX = 0, double scrollY = 0) =>
            Send(ComposeLiveProtocol.Pointer(kind, x, y, buttons, modifiers, button, scrollX, scrollY));

        public void SendKey(ComposeKeyKind kind, int windowsVirtualKey, int character, int modifiers) =>
            Send(ComposeLiveProtocol.Key(kind, windowsVirtualKey, character, modifiers));

        /// <summary>The host sends the next frame only after the previous one was acknowledged (or half a second passed).</summary>
        public void Acknowledge(ComposeLiveFrame frame) => Send(ComposeLiveProtocol.Ack(frame.Number));

        public void RequestFrame() => Send(ComposeLiveProtocol.Frame);

        private async Task<string?> StartHostAsync(ComposeLiveManifest current, CancellationToken token)
        {
            if (!File.Exists(current.JavaExecutable)) return "The JVM of the preview's target was not found: " + current.JavaExecutable;
            // A copy of the host classes: the project's "clean" must not pull them from under the running JVM.
            var classes = Path.Combine(WorkDirectory, "host");
            Directory.CreateDirectory(classes);
            foreach (var source in Directory.GetFiles(current.HostClasses, "*.class")) File.Copy(source, Path.Combine(classes, Path.GetFileName(source)), true);
            var arguments = Path.Combine(WorkDirectory, "host.args");
            File.WriteAllText(arguments, current.CreateArgumentFile(classes), new UTF8Encoding(false));
            var start = ProcessCommands.CreateRedirected(current.JavaExecutable, "\"@" + arguments + "\" \"" + WorkDirectory + "\"", WorkDirectory);
            var ready = new Pending(load: false, thenShown: false);
            OwnedProcess process;
            lock (sync)
            {
                pending = ready;
                host = process = new OwnedProcess(start);
                hostKey = current.HostKey;
            }
            _ = ReadAsync(process);
            _ = OwnedProcess.PumpAsync(process.Error, line => Log?.Invoke(line));
            _ = process.Exit.ContinueWith(exit => OnHostExit(process, exit.Result), TaskScheduler.Default);
            return await WaitAsync(ready, "The preview host did not start within 60 seconds.", token).ConfigureAwait(false);
        }

        private async Task<string?> RequestAsync(string command, Pending request, CancellationToken token)
        {
            lock (sync) pending = request;
            if (!Send(command))
            {
                lock (sync) if (pending == request) pending = null;
                return "The preview host is not running.";
            }
            return await WaitAsync(request, "The preview host did not answer within 60 seconds.", token).ConfigureAwait(false);
        }

        private async Task<string?> WaitAsync(Pending request, string timeoutMessage, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(60), timeout.Token);
                var done = await Task.WhenAny(request.Result.Task, delay).ConfigureAwait(false);
                timeout.Cancel();
                if (done != request.Result.Task)
                {
                    lock (sync) if (pending == request) pending = null;
                    token.ThrowIfCancellationRequested();
                    return timeoutMessage;
                }
                return await request.Result.Task.ConfigureAwait(false);
            }
        }

        private bool Send(string line)
        {
            OwnedProcess? process;
            lock (sync) process = host;
            if (process == null) return false;
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            try
            {
                lock (process)
                {
                    process.Input.Write(bytes, 0, bytes.Length);
                    process.Input.Flush();
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private async Task ReadAsync(OwnedProcess process)
        {
            try
            {
                while (true)
                {
                    var message = await ComposeLiveProtocol.ReadMessageAsync(process.Output, CancellationToken.None).ConfigureAwait(false);
                    if (message == null) return;
                    if (message.Value.Key == 'F') Frame?.Invoke(ComposeLiveProtocol.ParseFrame(message.Value.Value));
                    else if (message.Value.Key == 'T') OnText(ComposeLiveProtocol.ParseText(message.Value.Value));
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidDataException e) { Log?.Invoke("Compose live preview: " + e.Message); }
        }

        private void OnText(string[] field)
        {
            Pending? request;
            lock (sync) request = pending;
            switch (field[0])
            {
                case "ready":
                    if (request != null && !request.Load && !request.ThenShown) Complete(request, null);
                    break;
                case "loaded":
                    if (request != null && request.Load)
                    {
                        request.Loaded = true;
                        if (!request.ThenShown) Complete(request, null);
                    }
                    break;
                case "shown":
                    if (request != null && (!request.Load || request.Loaded)) Complete(request, null);
                    break;
                case "error":
                    var where = field.Length > 1 ? field[1] : "";
                    var message = field.Length > 2 ? field[2] : "Unknown error.";
                    // A command the host did not understand is the one that is waiting for its answer.
                    if (request != null && (where == "protocol" || where == "load" && request.Load || where == "show" && (!request.Load || request.Loaded))) Complete(request, message);
                    else Failed?.Invoke(where, message);
                    break;
            }
        }

        private void Complete(Pending request, string? error)
        {
            lock (sync) if (pending == request) pending = null;
            request.Result.TrySetResult(error);
        }

        private void OnHostExit(OwnedProcess process, int code)
        {
            Pending? request;
            bool current;
            lock (sync)
            {
                current = host == process;
                if (!current) return;
                host = null;
                hostKey = null;
                hostHasPreview = false;
                request = pending;
                pending = null;
            }
            request?.Result.TrySetResult("The preview host exited (code " + code.ToString(CultureInfo.InvariantCulture) + "). See Output > Kotlin for what it printed.");
            process.Dispose();
            if (!disposed) HostExited?.Invoke(code);
        }

        /// <summary>Ends the host JVM; the Gradle daemon stays for the next compile.</summary>
        public async Task StopHostAsync()
        {
            OwnedProcess? process;
            lock (sync)
            {
                process = host;
                host = null;
                hostKey = null;
            }
            hostHasPreview = false;
            if (process == null) return;
            try { await process.StopAsync().ConfigureAwait(false); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

        /// <summary>Ends the host and the Gradle daemon, and removes the session's files.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            OwnedProcess? process;
            Pending? request;
            lock (sync)
            {
                process = host;
                host = null;
                request = pending;
                pending = null;
            }
            request?.Result.TrySetResult("The preview was closed.");
            // Closing a job handle ends its processes.
            process?.Dispose();
            try { gradleJob.Terminate(); }
            catch (System.ComponentModel.Win32Exception) { }
            gradleJob.Dispose();
            var directory = WorkDirectory;
            Task.Run(async () =>
            {
                // The JVMs need a moment to let go of their files.
                for (var attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
                {
                    if (TryDelete(directory)) return;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            });
        }

        private static bool TryDelete(string directory)
        {
            try { Directory.Delete(directory, true); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
