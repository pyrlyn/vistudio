using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core.Diagnostics;

namespace Vistudio.Diagnostics
{
    [Guid("c7e1f3a5-2d4b-4c6e-8f0a-9b1d3e5f7a26")]
    public sealed class DiagnosticsWindow : ToolWindowPane
    {
        public DiagnosticsWindow() : base(null)
        {
            Caption = "Vistudio Diagnostics";
            Content = new DiagnosticsView();
        }
    }

    /// <summary>
    /// CPU and memory charts over the last five minutes (break periods shaded), the runtime heap, the events list and
    /// heap snapshots compared with the previous one. Redrawn once a second from the current session's timeline.
    /// </summary>
    internal sealed class DiagnosticsView : DockPanel
    {
        private static readonly TimeSpan Span = TimeSpan.FromMinutes(5);
        private static readonly Brush CpuBrush = Frozen(Color.FromRgb(0x3A, 0x96, 0xDD));
        private static readonly Brush PrivateBrush = Frozen(Color.FromRgb(0xE8, 0x91, 0x2D));
        private static readonly Brush WorkingSetBrush = Frozen(Color.FromRgb(0x88, 0x6C, 0xE4));
        private static readonly Brush HeapBrush = Frozen(Color.FromRgb(0x16, 0xA0, 0x4C));
        private static readonly Brush BreakBrush = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        private static readonly Brush GridBrush = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

        private readonly TextBlock title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        private readonly TextBlock cpuLabel = new TextBlock();
        private readonly TextBlock memoryLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Canvas cpu = Chart(80);
        private readonly Canvas memory = Chart(100);
        private readonly Button snapshotButton = new Button { Content = "Take Snapshot", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button stopButton = new Button { Content = "Stop", Padding = new Thickness(8, 2, 8, 2) };
        private readonly ListBox events = new ListBox { Height = 140, BorderThickness = new Thickness(1) };
        private readonly ComboBox snapshots = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        private readonly DataGrid classes = new DataGrid
        {
            Height = 240, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None, CanUserAddRows = false, Background = Brushes.Transparent, RowBackground = Brushes.Transparent,
        };
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private DiagnosticsMonitor? shown;
        private int shownEvents = -1, shownSnapshots = -1;
        private volatile bool dirty = true;

        public DiagnosticsView()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var control in new Control[] { events, classes })
            {
                control.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                control.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            }
            classes.Columns.Add(Column("Class", nameof(HeapClassDelta.Name), null, 220));
            classes.Columns.Add(Column("Objects", nameof(HeapClassDelta.Instances), "N0", 80));
            classes.Columns.Add(Column("Size", nameof(HeapClassDelta.Bytes), "bytes", 80));
            classes.Columns.Add(Column("Objects diff", nameof(HeapClassDelta.InstancesChange), "+#,0;-#,0;0", 90));
            classes.Columns.Add(Column("Size diff", nameof(HeapClassDelta.BytesChange), "diffbytes", 90));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            buttons.Children.Add(snapshotButton);
            buttons.Children.Add(stopButton);
            snapshotButton.Click += (_, __) => TakeSnapshot();
            stopButton.Click += (_, __) => shown?.Stop("Stopped from the Vistudio Diagnostics window.");
            snapshots.SelectionChanged += (_, __) => ShowSnapshot();

            var content = new StackPanel { Margin = new Thickness(8) };
            content.Children.Add(title);
            content.Children.Add(summary);
            content.Children.Add(Heading("CPU (% of all processors)"));
            content.Children.Add(cpuLabel);
            content.Children.Add(cpu);
            content.Children.Add(Heading("Memory"));
            content.Children.Add(memoryLabel);
            content.Children.Add(memory);
            content.Children.Add(buttons);
            content.Children.Add(Heading("Events"));
            content.Children.Add(events);
            content.Children.Add(Heading("Snapshots (largest size changes since the previous snapshot first)"));
            content.Children.Add(snapshots);
            content.Children.Add(classes);
            Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

            DiagnosticsSessions.Changed += MarkDirty;
            timer.Tick += (_, __) => Refresh();
            Loaded += (_, __) => { timer.Start(); Refresh(); };
            Unloaded += (_, __) => timer.Stop();
            SizeChanged += (_, __) => { dirty = true; Refresh(); };
        }

        private void MarkDirty() => dirty = true;

        private void Refresh()
        {
            var monitor = DiagnosticsSessions.Current;
            if (monitor != shown)
            {
                if (shown != null) shown.Timeline.Changed -= MarkDirty;
                shown = monitor;
                if (shown != null) shown.Timeline.Changed += MarkDirty;
                shownEvents = shownSnapshots = -1;
                dirty = true;
            }
            if (!dirty) return;
            dirty = false;
            title.Text = monitor?.Timeline.Title ?? "No diagnostics session";
            var status = DiagnosticsSessions.Status;
            snapshotButton.IsEnabled = monitor?.IsRunning == true;
            stopButton.IsEnabled = monitor?.IsRunning == true;
            if (monitor == null)
            {
                summary.Text = status;
                cpu.Children.Clear(); memory.Children.Clear(); events.Items.Clear(); snapshots.Items.Clear(); classes.ItemsSource = null;
                cpuLabel.Text = memoryLabel.Text = "";
                return;
            }
            var timeline = monitor.Timeline;
            var samples = timeline.Samples;
            var latest = samples.Count == 0 ? null : samples[samples.Count - 1];
            var pendingNote = status != timeline.Title ? status + "  " : "";
            summary.Text = pendingNote + (latest == null ? "Waiting for the first sample..."
                : (monitor.IsRunning ? (latest.Broken ? "In break mode. " : "Running. ") : "Ended. ") + latest.Processes + " process" + (latest.Processes == 1 ? "" : "es") + ".");
            cpuLabel.Text = latest?.CpuPercent is double c ? c.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "";
            memoryLabel.Inlines.Clear();
            if (latest != null)
            {
                Legend(PrivateBrush, "Private bytes " + DiagnosticsTimeline.FormatBytes(latest.PrivateBytes));
                Legend(WorkingSetBrush, "Working set " + DiagnosticsTimeline.FormatBytes(latest.WorkingSet));
                if (latest.Heap != null)
                    Legend(HeapBrush, latest.Heap.Runtime + " " + DiagnosticsTimeline.FormatBytes(latest.Heap.Used) + " of " + DiagnosticsTimeline.FormatBytes(latest.Heap.Capacity) +
                        (latest.Heap.Collections is long n ? ", " + n + " GCs" : "") + (latest.Heap.External is long x && x > 0 ? ", external " + DiagnosticsTimeline.FormatBytes(x) : ""));
            }
            DrawCpu(samples);
            DrawMemory(samples);
            var eventList = timeline.Events;
            if (eventList.Count != shownEvents)
            {
                shownEvents = eventList.Count;
                events.Items.Clear();
                foreach (var e in eventList.Reverse()) events.Items.Add(e.ToString());
            }
            var snapshotList = timeline.Snapshots;
            if (snapshotList.Count != shownSnapshots)
            {
                shownSnapshots = snapshotList.Count;
                snapshots.Items.Clear();
                foreach (var s in snapshotList)
                    snapshots.Items.Add(new ComboBoxItem
                    {
                        Content = "#" + s.Number + "  " + s.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "  " +
                            (s.Classes.Count == 0 ? "private bytes " + DiagnosticsTimeline.FormatBytes(s.PrivateBytes) : DiagnosticsTimeline.FormatBytes(s.TotalBytes) + " in " + s.TotalInstances.ToString("N0", CultureInfo.CurrentCulture) + " objects") +
                            "  (" + s.Source + ")",
                        Tag = s,
                    });
                if (snapshots.Items.Count != 0) snapshots.SelectedIndex = snapshots.Items.Count - 1;
            }
        }

        private void ShowSnapshot()
        {
            if (!(snapshots.SelectedItem is ComboBoxItem item && item.Tag is HeapSnapshot snapshot) || shown == null) { classes.ItemsSource = null; return; }
            var previous = shown.Timeline.Snapshots.LastOrDefault(s => s.Number < snapshot.Number);
            classes.ItemsSource = snapshot.Compare(previous).Take(500).ToList();
        }

        private void TakeSnapshot()
        {
            var monitor = shown;
            if (monitor == null) return;
            snapshotButton.IsEnabled = false;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                        await Task.Run(() => monitor.TakeSnapshotAsync(timeout.Token));
                }
                catch (Exception e) { monitor.Note(DiagnosticsEventKind.Note, "Snapshot failed: " + e.Message); }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                snapshotButton.IsEnabled = monitor.IsRunning;
                dirty = true;
                Refresh();
            }).FileAndForget("Vistudio/Diagnostics/Snapshot");
        }

        private void DrawCpu(IReadOnlyList<UsageSample> samples)
        {
            cpu.Children.Clear();
            var (start, width, height) = Frame(cpu, samples);
            if (width <= 0) return;
            Shade(cpu, samples, start, width, height);
            Line(cpu, samples.Where(s => s.CpuPercent != null), s => s.CpuPercent!.Value / 100, start, width, height, CpuBrush, false);
        }

        private void DrawMemory(IReadOnlyList<UsageSample> samples)
        {
            memory.Children.Clear();
            var (start, width, height) = Frame(memory, samples);
            if (width <= 0 || samples.Count == 0) return;
            var max = samples.Max(s => Math.Max(Math.Max(s.PrivateBytes, s.WorkingSet), s.Heap?.Capacity ?? 0)) * 1.1;
            if (max <= 0) return;
            Shade(memory, samples, start, width, height);
            Line(memory, samples, s => s.PrivateBytes / max, start, width, height, PrivateBrush, false);
            Line(memory, samples, s => s.WorkingSet / max, start, width, height, WorkingSetBrush, false);
            var heap = samples.Where(s => s.Heap != null).ToList();
            Line(memory, heap, s => s.Heap!.Used / max, start, width, height, HeapBrush, false);
            Line(memory, heap, s => s.Heap!.Capacity / max, start, width, height, HeapBrush, true);
        }

        private static (DateTime Start, double Width, double Height) Frame(Canvas canvas, IReadOnlyList<UsageSample> samples)
        {
            var width = canvas.ActualWidth;
            var height = canvas.Height;
            var end = samples.Count == 0 ? DateTime.Now : samples[samples.Count - 1].Time;
            for (var i = 1; i < 4; i++)
                canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height * i / 4, Y2 = height * i / 4, Stroke = GridBrush, StrokeThickness = 1 });
            return (end - Span, width, height);
        }

        private static double X(DateTime time, DateTime start, double width) => Math.Max(0, (time - start).Ticks / (double)Span.Ticks * width);

        private static void Shade(Canvas canvas, IReadOnlyList<UsageSample> samples, DateTime start, double width, double height)
        {
            for (var i = 0; i < samples.Count; i++)
            {
                if (!samples[i].Broken) continue;
                var first = i;
                while (i + 1 < samples.Count && samples[i + 1].Broken) i++;
                var left = X(samples[first].Time, start, width);
                var right = X(i + 1 < samples.Count ? samples[i + 1].Time : samples[i].Time, start, width);
                var block = new Rectangle { Width = Math.Max(2, right - left), Height = height, Fill = BreakBrush, ToolTip = "Debugger break" };
                Canvas.SetLeft(block, left);
                canvas.Children.Add(block);
            }
        }

        private static void Line(Canvas canvas, IEnumerable<UsageSample> samples, Func<UsageSample, double> value, DateTime start, double width, double height, Brush brush, bool dashed)
        {
            var points = new PointCollection(samples.Where(s => s.Time >= start).Select(s => new Point(X(s.Time, start, width), height - Math.Min(1, Math.Max(0, value(s))) * height)));
            if (points.Count < 2) return;
            var line = new Polyline { Points = points, Stroke = brush, StrokeThickness = 1.5 };
            if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 2 };
            canvas.Children.Add(line);
        }

        private void Legend(Brush brush, string text)
        {
            if (memoryLabel.Inlines.Count != 0) memoryLabel.Inlines.Add(new Run("    "));
            memoryLabel.Inlines.Add(new Run("■ ") { Foreground = brush });
            memoryLabel.Inlines.Add(new Run(text));
        }

        private static Canvas Chart(double height) => new Canvas { Height = height, ClipToBounds = true, Margin = new Thickness(0, 2, 0, 4), Background = Brushes.Transparent };

        private static TextBlock Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static DataGridTextColumn Column(string header, string path, string? format, double width)
        {
            var binding = new Binding(path);
            if (format == "bytes" || format == "diffbytes") binding.Converter = new BytesConverter(format == "diffbytes");
            else if (format != null) binding.StringFormat = "{0:" + format + "}";
            return new DataGridTextColumn { Header = header, Binding = binding, Width = new DataGridLength(width) };
        }

        private sealed class BytesConverter : IValueConverter
        {
            private readonly bool signed;
            public BytesConverter(bool signed) { this.signed = signed; }
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var bytes = value is long l ? l : 0;
                return (signed && bytes > 0 ? "+" : "") + DiagnosticsTimeline.FormatBytes(bytes);
            }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}
