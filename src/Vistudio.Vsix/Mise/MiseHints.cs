using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Mise;

namespace Vistudio.Mise
{
    // End-of-line hints on the tool lines of mise config files: "not installed", "current → in range · latest", or the
    // installed version. Drawn on an adornment layer, so the text, its layout and the caret are untouched.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("any"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class MiseHintsListener : IWpfTextViewCreationListener
    {
        internal const string Layer = "VistudioMiseHints";

        [Export(typeof(AdornmentLayerDefinition)), Name(Layer), Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
        internal AdornmentLayerDefinition? LayerDefinition = null;

        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal IEditorFormatMapService FormatMaps = null!;
        // Lazy: every editor passes through here, only mise configs need the service.
        [Import] internal Lazy<MiseService> Service = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            if (!Documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document) || !MiseConfigFiles.IsConfigFile(document.FilePath)) return;
            view.Properties.GetOrCreateSingletonProperty(() => new MiseHints(view, document, Service.Value, FormatMaps.GetEditorFormatMap(view)));
        }
    }

    internal static class MiseHintFormats
    {
        internal const string Update = "Vistudio Mise Update";
        internal const string Missing = "Vistudio Mise Missing";
        internal const string Current = "Vistudio Mise Current";

        internal static Color Default(string name) =>
            name == Missing ? Color.FromRgb(0xE0, 0x8A, 0x1E) : name == Update ? Color.FromRgb(0x2E, 0xA0, 0x43) : Color.FromRgb(0x8A, 0x8A, 0x8A);

        [Export(typeof(EditorFormatDefinition)), Name(Update), UserVisible(true)]
        internal sealed class UpdateFormat : EditorFormatDefinition
        { public UpdateFormat() { DisplayName = "Mise - Update available"; ForegroundColor = Default(Update); } }

        [Export(typeof(EditorFormatDefinition)), Name(Missing), UserVisible(true)]
        internal sealed class MissingFormat : EditorFormatDefinition
        { public MissingFormat() { DisplayName = "Mise - Not installed"; ForegroundColor = Default(Missing); } }

        [Export(typeof(EditorFormatDefinition)), Name(Current), UserVisible(true)]
        internal sealed class CurrentFormat : EditorFormatDefinition
        { public CurrentFormat() { DisplayName = "Mise - Up to date"; ForegroundColor = Default(Current); } }
    }

    internal sealed class MiseHints
    {
        private const double Gap = 24;
        private readonly IWpfTextView view;
        private readonly ITextDocument document;
        private readonly MiseService service;
        private readonly IEditorFormatMap formats;
        private readonly IAdornmentLayer layer;
        private readonly Dictionary<string, Brush> brushes = new Dictionary<string, Brush>();
        private ITextSnapshot? scanned;
        private MiseToolLayout? layout;
        private bool pending, closed;

        internal MiseHints(IWpfTextView view, ITextDocument document, MiseService service, IEditorFormatMap formats)
        {
            this.view = view; this.document = document; this.service = service; this.formats = formats;
            layer = view.GetAdornmentLayer(MiseHintsListener.Layer);
            view.LayoutChanged += LayoutChanged;
            view.Closed += Closed;
            document.FileActionOccurred += FileActionOccurred;
            formats.FormatMappingChanged += FormatsChanged;
            service.ReportChanged += ReportChanged;
            MiseService.SettingsChanged += SettingsChanged;
            service.GetReportAsync(Folder).Forget();
        }

        private string Path => document.FilePath;
        private string Folder => MiseConfigFiles.ProjectFolder(Path);

        private void LayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) => Redraw();
        private void FormatsChanged(object? sender, FormatItemsEventArgs e) { brushes.Clear(); Schedule(); }
        private void SettingsChanged(object? sender, EventArgs e) => Schedule();

        private void ReportChanged(object? sender, string folder)
        {
            if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase)) Schedule();
        }

        private void FileActionOccurred(object? sender, TextDocumentFileActionEventArgs e)
        {
            // A saved config changes what mise reports (also after `mise upgrade --bump` rewrote it and the editor reloaded).
            if ((e.FileActionType & (FileActionTypes.ContentSavedToDisk | FileActionTypes.ContentLoadedFromDisk)) != 0)
                service.GetReportAsync(Folder, force: true).Forget();
            if ((e.FileActionType & FileActionTypes.DocumentRenamed) != 0) Schedule();
        }

        private void Schedule()
        {
            if (pending || closed) return;
            pending = true;
            RedrawLaterAsync().Forget();
        }

        private async System.Threading.Tasks.Task RedrawLaterAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            pending = false;
            Redraw();
        }

        private void Redraw()
        {
            if (closed) return;
            if (view.InLayout) { Schedule(); return; }
            layer.RemoveAllAdornments();
            if (!MiseService.Settings.InlineHints || !MiseConfigFiles.IsConfigFile(Path)) return;
            var lines = view.TextViewLines;
            if (lines == null || lines.Count == 0) return;
            var snapshot = view.TextSnapshot;
            if (scanned != snapshot)
            {
                layout = MiseToolLocator.Find(snapshot.GetText(), MiseConfigFiles.IsToolVersions(Path));
                scanned = snapshot;
            }
            if (layout == null || layout.HeaderLine == null) return;

            var folder = Folder;
            var report = service.Cached(folder);
            if (report == null || report.CreatedUtc == DateTime.MinValue) service.GetReportAsync(folder).Forget();
            int first = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber, last = lines.LastVisibleLine.End.GetContainingLine().LineNumber;

            if (report == null)
            {
                Label(snapshot, lines, layout.HeaderLine.Value, first, last, "mise: checking tools…", "mise is reading this config.", MiseHintFormats.Current);
                return;
            }
            if (report.Tools.Count == 0 && report.Error != null)
            {
                Label(snapshot, lines, layout.HeaderLine.Value, first, last, "⚠ mise: " + Shorten(report.Error), report.Error, MiseHintFormats.Missing);
                return;
            }
            var own = report.ForFile(Path);
            var labeled = new HashSet<int>();
            foreach (var entry in layout.Tools)
            {
                if (entry.Line < first || entry.Line > last || !labeled.Add(entry.Line)) continue;
                var status = MiseReport.Match(own, entry.Name);
                if (status == null)
                {
                    // Defined here but another config wins (mise.local.toml, a sub-folder, an environment variable).
                    var winner = MiseReport.Match(report.Tools, entry.Name);
                    if (winner != null)
                        Label(snapshot, lines, entry.Line, first, last, "overridden: " + winner.Version + " from " + System.IO.Path.GetFileName(winner.SourcePath ?? "another config"),
                            winner.Tooltip(), MiseHintFormats.Current);
                    continue;
                }
                switch (status.State)
                {
                    case MiseToolState.Missing:
                        Label(snapshot, lines, entry.Line, first, last, "⚠ " + status.Hint(), status.Tooltip(), MiseHintFormats.Missing);
                        break;
                    case MiseToolState.UpdateAvailable:
                        Label(snapshot, lines, entry.Line, first, last, "↑ " + status.Hint(), status.Tooltip(), MiseHintFormats.Update);
                        break;
                    default:
                        if (MiseService.Settings.ShowUpToDate)
                            Label(snapshot, lines, entry.Line, first, last, "✓ " + status.Hint(), status.Tooltip(), MiseHintFormats.Current);
                        break;
                }
            }
            if (report.Error != null && !labeled.Contains(layout.HeaderLine.Value))
                Label(snapshot, lines, layout.HeaderLine.Value, first, last, "⚠ " + Shorten(report.Error), report.Error, MiseHintFormats.Missing);
        }

        private static string Shorten(string text) => text.Length > 120 ? text.Substring(0, 117) + "..." : text;

        private void Label(ITextSnapshot snapshot, IWpfTextViewLineCollection lines, int lineNumber, int first, int last, string text, string tooltip, string format)
        {
            if (lineNumber < first || lineNumber > last || lineNumber >= snapshot.LineCount) return;
            var line = snapshot.GetLineFromLineNumber(lineNumber);
            // With word wrap a line spans several view lines: label the last.
            var viewLine = lines.GetTextViewLineContainingBufferPosition(line.End);
            if (viewLine == null || viewLine.VisibilityState == VisibilityState.Hidden) return;
            var label = new TextBlock
            {
                Text = text, ToolTip = tooltip, Foreground = Brush(format), FontStyle = FontStyles.Italic,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(240, view.ViewportRight - viewLine.TextRight - Gap - 8),
            };
            var properties = view.FormattedLineSource?.DefaultTextProperties;
            if (properties != null)
            {
                label.FontFamily = properties.Typeface.FontFamily;
                label.FontSize = properties.FontRenderingEmSize;
            }
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, viewLine.TextRight + Gap);
            Canvas.SetTop(label, viewLine.TextTop + Math.Max(0, (viewLine.TextHeight - label.DesiredSize.Height) / 2));
            layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, label, null);
        }

        private Brush Brush(string format)
        {
            if (brushes.TryGetValue(format, out var cached)) return cached;
            var color = MiseHintFormats.Default(format);
            var map = formats.GetProperties(format);
            if (map.Contains(EditorFormatDefinition.ForegroundColorId) && map[EditorFormatDefinition.ForegroundColorId] is Color custom) color = custom;
            else if (map.Contains(EditorFormatDefinition.ForegroundBrushId) && map[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush brush) color = brush.Color;
            var result = new SolidColorBrush(color);
            result.Freeze();
            return brushes[format] = result;
        }

        private void Closed(object? sender, EventArgs e)
        {
            if (closed) return;
            closed = true;
            view.LayoutChanged -= LayoutChanged; view.Closed -= Closed;
            document.FileActionOccurred -= FileActionOccurred;
            formats.FormatMappingChanged -= FormatsChanged;
            service.ReportChanged -= ReportChanged;
            MiseService.SettingsChanged -= SettingsChanged;
        }
    }
}
