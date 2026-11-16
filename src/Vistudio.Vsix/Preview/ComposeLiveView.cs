using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Preview;

namespace Vistudio.Preview
{
    /// <summary>
    /// The interactive part of the Kotlin Compose Preview window: one @Preview function running in a host JVM
    /// (<see cref="ComposeLiveSession"/>). Shows its frames, sends it the mouse and, while focused, the keyboard, and
    /// reloads it after a save. Gradle runs through the package's shared gate like every other build.
    /// </summary>
    internal sealed class ComposeLiveView : Grid
    {
        private readonly KotlinPackage package;
        private readonly Action<string> setStatus;
        private readonly Button back = new Button { Content = "Back to previews", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 2, 4, 2) };
        private readonly Button restart = new Button { Content = "Restart", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 2, 4, 2),
            ToolTip = "Show the preview again from its initial state." };
        private readonly TextBlock title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        private readonly TextBox error = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), FontFamily = new FontFamily("Consolas"), Margin = new Thickness(8, 4, 8, 4),
            MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly Image image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        private readonly Border canvas = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        // Takes the keyboard focus; the frame around it shows where the keys go.
        private readonly Border surface = new Border { Focusable = true, FocusVisualStyle = null, Background = Brushes.Transparent, BorderThickness = new Thickness(1),
            Padding = new Thickness(7), Margin = new Thickness(8, 4, 8, 8), Cursor = Cursors.Arrow,
            ToolTip = "Click to interact. While the preview has the keyboard, typing goes to it; Escape returns to the editor." };
        private static readonly Brush Idle = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        private static readonly Brush Focused = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));

        private ComposeLiveSession? session;
        private string? file;
        private ComposePreviewFile? previews;
        private ComposePreview? preview;
        private WriteableBitmap? bitmap;
        private ComposeLiveFrame? pendingFrame;
        private int blitQueued;
        private CancellationTokenSource? running;
        private bool reloadPending;
        private bool pointerInside;
        private bool stopped = true;

        public ComposeLiveView(KotlinPackage package, Action<string> setStatus)
        {
            this.package = package;
            this.setStatus = setStatus;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(back);
            header.Children.Add(restart);
            header.Children.Add(title);
            SetRow(error, 1);
            canvas.Child = image;
            // Shown at its dp size; a preview larger than the window shrinks to fit. Pointer positions are taken
            // relative to the image, so they stay frame pixels at any size.
            surface.Child = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            surface.BorderBrush = Idle;
            SetRow(surface, 2);
            Children.Add(header);
            Children.Add(error);
            Children.Add(surface);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            // Ids for UI Automation (tests/ComposeLiveAcceptance.cs).
            System.Windows.Automation.AutomationProperties.SetAutomationId(back, "ComposePreview.Back");
            System.Windows.Automation.AutomationProperties.SetAutomationId(restart, "ComposePreview.Restart");
            System.Windows.Automation.AutomationProperties.SetAutomationId(error, "ComposePreview.LiveError");
            System.Windows.Automation.AutomationProperties.SetAutomationId(image, "ComposePreview.LiveImage");

            back.Click += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); Stop(); Closed?.Invoke(); };
            restart.Click += (_, __) => { var compile = session?.Manifest == null; Run(() => ShowPreviewAsync(compile)); };
            surface.MouseDown += (_, e) => surface.Focus();
            surface.IsKeyboardFocusWithinChanged += (_, __) => surface.BorderBrush = surface.IsKeyboardFocusWithin ? Focused : Idle;
            image.MouseMove += (_, e) => Pointer(ComposePointerKind.Move, e);
            image.MouseDown += (_, e) => { image.CaptureMouse(); Pointer(ComposePointerKind.Press, e, Button(e.ChangedButton)); };
            image.MouseUp += (_, e) => { Pointer(ComposePointerKind.Release, e, Button(e.ChangedButton)); if (Buttons() == 0) image.ReleaseMouseCapture(); };
            image.MouseWheel += (_, e) =>
            {
                // A wheel notch is 120 and one scroll unit; away from the user scrolls up.
                var notches = -e.Delta / 120.0;
                var horizontal = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                Pointer(ComposePointerKind.Scroll, e, -1, horizontal ? notches : 0, horizontal ? 0 : notches);
                e.Handled = true;
            };
            image.MouseEnter += (_, __) => pointerInside = true;
            image.MouseLeave += (_, __) =>
            {
                if (!pointerInside) return;
                pointerInside = false;
                session?.SendPointer(ComposePointerKind.Exit, -1, -1, Buttons(), Modifiers());
            };
            // A hidden window takes no frames, which slows the host to two frames a second; ask for one when it returns.
            IsVisibleChanged += (_, __) => { if (IsVisible) session?.RequestFrame(); };
        }

        /// <summary>The user left the interactive preview with its Back button.</summary>
        public event Action? Closed;

        /// <summary>True from Start until Stop, also while Gradle still compiles for the first frame.</summary>
        public bool Active => !stopped;
        /// <summary>True while key presses belong to the preview (see <see cref="HandleKey"/>).</summary>
        public bool HasKeyboard => session != null && surface.IsKeyboardFocusWithin;
        public bool Busy => running != null;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Each action reports its own failure in the window and the Kotlin pane, so it can run detached.
#pragma warning disable VSSDK007
        private void Run(Func<Task> action) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await action(); }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    // A step that was running when the preview was closed fails with its session; that is not an error to show.
                    if (stopped) return;
                    ShowError("Compose preview: " + e.Message);
                    // No folder open, or Gradle busy: a state the window reports, not a failure of the extension.
                    if (e is InvalidOperationException) return;
                    package.Environment.Log("Compose live preview: " + e);
                    ActivityLog.TryLogError("Vistudio.ComposePreview", e.ToString());
                }
            }).Task.Forget();
#pragma warning restore VSSDK007

        /// <summary>Starts (or switches to) the interactive preview of one function.</summary>
        public void Start(string path, ComposePreviewFile file, ComposePreview preview)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            stopped = false;
            this.file = path;
            previews = file;
            this.preview = preview;
            title.Text = preview.DisplayName;
            canvas.Background = preview.ShowBackground
                ? Frozen(preview.BackgroundColor == 0 ? Colors.White : Color.FromArgb((byte)(preview.BackgroundColor >> 24), (byte)(preview.BackgroundColor >> 16),
                    (byte)(preview.BackgroundColor >> 8), (byte)preview.BackgroundColor))
                : null;
            image.Source = bitmap = null;
            Run(() => ShowPreviewAsync(compile: true));
        }

        /// <summary>A source file was saved: compile and load the new classes. The preview starts over with the new code.</summary>
        public void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (stopped || preview == null) return;
            Run(() => ShowPreviewAsync(compile: true));
        }

        public void Cancel() => running?.Cancel();

        private async Task ShowPreviewAsync(bool compile)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = file;
            var shownFile = previews;
            var shown = preview;
            if (path == null || shownFile == null || shown == null) return;
            if (running != null)
            {
                // One at a time; what was asked for meanwhile runs when the current step finishes.
                reloadPending = true;
                return;
            }
            running = new CancellationTokenSource();
            var token = running.Token;
            restart.IsEnabled = false;
            try
            {
                if (session == null)
                {
                    var (gradleRoot, javaHome, build) = await ComposePreviewRunner.LocateAsync(package.Environment, token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    session = new ComposeLiveSession(gradleRoot, javaHome, Path.Combine(build, "ComposePreview.gradle"), Path.Combine(build, "VistudioPreviewRenderer.java"));
                    session.Frame += OnFrame;
                    session.Log += line => package.Environment.Log("Compose preview host: " + line);
                    session.Failed += OnFailed;
                    session.HostExited += OnHostExited;
                    compile = true;
                }
                var current = session;
                if (compile)
                {
                    while (package.GradleBusy)
                    {
                        Status("Waiting for the running Gradle operation to finish…");
                        await Task.Delay(1000, token);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }
                    Status("Compiling " + Path.GetFileName(path) + " with Gradle…");
                    var compileError = await package.RunExclusiveGradleAsync(async gradleToken =>
                    {
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, gradleToken))
                            return await current.CompileAsync(path, package.Environment.Log, linked.Token);
                    });
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (session != current) return;
                    if (compileError != null)
                    {
                        // The preview from before the edit keeps running; the error says why it is not the new code.
                        ShowError(compileError);
                        Status(Path.GetFileName(path) + ": " + FirstLine(compileError));
                        return;
                    }
                    Status("Starting " + shown.DisplayName + "…");
                    var loadError = await current.LoadAsync(token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (session != current) return;
                    // With a running host a failed load is the previous preview failing with the new code; it is shown again below.
                    if (loadError != null && current.HostProcessId == null)
                    {
                        ShowError(loadError);
                        Status(shown.DisplayName + ": " + FirstLine(loadError));
                        return;
                    }
                }
                var scale = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
                var showError = await current.ShowAsync(shownFile.JvmClass, shown.Function, shown.WidthDp, shown.HeightDp, scale, token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session != current) return;
                if (showError != null)
                {
                    ShowError(showError);
                    Status(shown.DisplayName + ": " + FirstLine(showError));
                }
                else
                {
                    error.Visibility = Visibility.Collapsed;
                    Status(shown.DisplayName + ": interactive since " + DateTime.Now.ToString("T") + ".");
                }
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Status("Canceled.");
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                running?.Dispose();
                running = null;
                restart.IsEnabled = true;
                var again = reloadPending;
                reloadPending = false;
                if (again && session != null) Run(() => ShowPreviewAsync(compile: true));
            }
        }

        /// <summary>Ends the host and the Gradle daemon of the preview.</summary>
        public void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            stopped = true;
            running?.Cancel();
            reloadPending = false;
            var old = session;
            session = null;
            pendingFrame = null;
            image.Source = bitmap = null;
            error.Visibility = Visibility.Collapsed;
            if (old == null) return;
            old.Frame -= OnFrame;
            old.Failed -= OnFailed;
            old.HostExited -= OnHostExited;
            old.Dispose();
        }

        private void Status(string message)
        {
            setStatus(message);
            ActivityLog.TryLogInformation("Vistudio.ComposePreview", message); // recorded only when Visual Studio runs with /Log
        }

        private void ShowError(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
            ActivityLog.TryLogInformation("Vistudio.ComposePreview", "Live preview error: " + message);
        }

        private static string FirstLine(string text)
        {
            var end = text.IndexOf('\n');
            return end < 0 ? text : text.Substring(0, end).TrimEnd('\r');
        }

        // ---- frames ----

        private void OnFrame(ComposeLiveFrame frame)
        {
            // Thread pool. Only the newest frame matters; one switch to the UI thread takes whatever is newest then.
            Interlocked.Exchange(ref pendingFrame, frame);
            if (Interlocked.Exchange(ref blitQueued, 1) != 0) return;
#pragma warning disable VSSDK007 // Blit cannot fail in a way worth reporting; the next frame draws again.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Blit();
            }).Task.Forget();
#pragma warning restore VSSDK007
        }

        private void Blit()
        {
            Interlocked.Exchange(ref blitQueued, 0);
            var frame = Interlocked.Exchange(ref pendingFrame, null);
            var current = session;
            if (frame == null || current == null || !IsVisible) return;
            if (bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height || Math.Abs(bitmap.DpiX - 96 * frame.Scale) > 0.01)
            {
                bitmap = new WriteableBitmap(frame.Width, frame.Height, 96 * frame.Scale, 96 * frame.Scale, PixelFormats.Pbgra32, null);
                image.Source = bitmap;
                image.Width = frame.Width / frame.Scale;
                image.Height = frame.Height / frame.Scale;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Buffer, frame.Width * 4, frame.Offset);
            current.Acknowledge(frame);
        }

#pragma warning disable VSSDK007
        private void OnFailed(string where, string message) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session == null) return;
                ShowError(message);
                Status((preview?.DisplayName ?? "Preview") + " stopped (" + where + "): " + FirstLine(message) + " Click Restart to run it again.");
            }).Task.Forget();

        private void OnHostExited(int code) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (session == null) return;
                ShowError("The preview host exited (code " + code + "). See Output > Kotlin for what it printed. Click Restart to start it again.");
                Status("The preview host exited.");
            }).Task.Forget();
#pragma warning restore VSSDK007

        // ---- input ----

        private void Pointer(ComposePointerKind kind, MouseEventArgs e, int button = -1, double scrollX = 0, double scrollY = 0)
        {
            var current = session;
            var frame = bitmap;
            if (current == null || frame == null || image.ActualWidth <= 0 || image.ActualHeight <= 0) return;
            var position = e.GetPosition(image);
            current.SendPointer(kind, position.X * frame.PixelWidth / image.ActualWidth, position.Y * frame.PixelHeight / image.ActualHeight,
                Buttons(), Modifiers(), button, scrollX, scrollY);
        }

        private static int Button(MouseButton button) => button == MouseButton.Left ? 0 : button == MouseButton.Right ? 1 : button == MouseButton.Middle ? 2 : -1;

        private static int Buttons() =>
            (Mouse.LeftButton == MouseButtonState.Pressed ? ComposeLiveProtocol.ButtonPrimary : 0)
            | (Mouse.RightButton == MouseButtonState.Pressed ? ComposeLiveProtocol.ButtonSecondary : 0)
            | (Mouse.MiddleButton == MouseButtonState.Pressed ? ComposeLiveProtocol.ButtonTertiary : 0);

        private static int Modifiers()
        {
            var keys = Keyboard.Modifiers;
            return ((keys & ModifierKeys.Shift) != 0 ? ComposeLiveProtocol.ModifierShift : 0) | ((keys & ModifierKeys.Control) != 0 ? ComposeLiveProtocol.ModifierControl : 0)
                | ((keys & ModifierKeys.Alt) != 0 ? ComposeLiveProtocol.ModifierAlt : 0) | ((keys & ModifierKeys.Windows) != 0 ? ComposeLiveProtocol.ModifierMeta : 0);
        }

        /// <summary>
        /// A key message of the tool window while the preview has the keyboard. Returns true when the preview took it:
        /// the IDE must then neither run a command for it nor dispatch it. The typed character is derived here from
        /// the key and the keyboard state, because a key the IDE does not dispatch produces no WM_CHAR.
        /// Dead keys and input methods are not supported.
        /// </summary>
        public bool HandleKey(int message, IntPtr wParam, IntPtr lParam)
        {
            const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
            var current = session;
            if (current == null || message != WM_KEYDOWN && message != WM_KEYUP) return false;
            var virtualKey = wParam.ToInt32();
            var modifiers = Modifiers();
            if (!ComposeLiveProtocol.GoesToPreview(virtualKey, modifiers)) return false;
            if (message == WM_KEYUP)
            {
                current.SendKey(ComposeKeyKind.Release, virtualKey, 0, modifiers);
                return true;
            }
            current.SendKey(ComposeKeyKind.Press, virtualKey, 0, modifiers);
            if ((modifiers & ComposeLiveProtocol.ModifierControl) == 0)
                foreach (var ch in Typed(virtualKey, (lParam.ToInt64() >> 16) & 0xFF))
                    current.SendKey(ComposeKeyKind.Typed, 0, ch, modifiers);
            return true;
        }

        private static string Typed(int virtualKey, long scanCode)
        {
            var state = new byte[256];
            if (!GetKeyboardState(state)) return "";
            var text = new StringBuilder(8);
            // Flag 4: do not change the keyboard state (no dead-key side effects).
            var count = ToUnicodeEx((uint)virtualKey, (uint)scanCode, state, text, text.Capacity, 4, GetKeyboardLayout(0));
            if (count <= 0) return "";
            var result = text.ToString(0, Math.Min(count, text.Length));
            foreach (var ch in result) if (ch < 0x20 || ch == 0x7F) return "";
            return result;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] state, [Out] StringBuilder buffer, int size, uint flags, IntPtr layout);
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint thread);
    }
}
