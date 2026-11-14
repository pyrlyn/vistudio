using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Vistudio.Core;

namespace Vistudio
{
    // Error Lens: severity-colored diagnostic messages after the end of each line plus a whole-line tint,
    // for every editor. Drawn on adornment layers, so it never changes text layout or caret positions.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("any"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ErrorLensListener : IWpfTextViewCreationListener
    {
        internal const string TintLayer = "VistudioErrorLensTint";
        internal const string MessageLayer = "VistudioErrorLensMessage";

        [Export(typeof(AdornmentLayerDefinition)), Name(TintLayer), Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        internal AdornmentLayerDefinition? TintLayerDefinition = null;

        [Export(typeof(AdornmentLayerDefinition)), Name(MessageLayer), Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Caret)]
        internal AdornmentLayerDefinition? MessageLayerDefinition = null;

        [Import] internal IViewTagAggregatorFactoryService Aggregators = null!;
        [Import] internal IEditorFormatMapService FormatMaps = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            if (!KotlinSettings.Load().EditorDiagnostics) return;
            view.Properties.GetOrCreateSingletonProperty(() => new ErrorLensAdornment(view, Aggregators.CreateTagAggregator<IErrorTag>(view), FormatMaps.GetEditorFormatMap(view)));
        }
    }

    internal static class ErrorLensFormats
    {
        internal const string Error = "Vistudio Error Lens Error";
        internal const string Warning = "Vistudio Error Lens Warning";
        internal const string Info = "Vistudio Error Lens Info";

        internal static string Name(ErrorLensSeverity severity) =>
            severity == ErrorLensSeverity.Error ? Error : severity == ErrorLensSeverity.Warning ? Warning : Info;

        // Mid-tone defaults read on both light and dark themes; users override them in Fonts and Colors.
        internal static Color Default(ErrorLensSeverity severity) =>
            severity == ErrorLensSeverity.Error ? Color.FromRgb(0xE5, 0x48, 0x4D) :
            severity == ErrorLensSeverity.Warning ? Color.FromRgb(0xE0, 0x8A, 0x1E) : Color.FromRgb(0x1E, 0x9F, 0xD6);

        [Export(typeof(EditorFormatDefinition)), Name(Error), UserVisible(true)]
        internal sealed class ErrorFormat : EditorFormatDefinition
        { public ErrorFormat() { DisplayName = "Error Lens - Error"; ForegroundColor = Default(ErrorLensSeverity.Error); } }

        [Export(typeof(EditorFormatDefinition)), Name(Warning), UserVisible(true)]
        internal sealed class WarningFormat : EditorFormatDefinition
        { public WarningFormat() { DisplayName = "Error Lens - Warning"; ForegroundColor = Default(ErrorLensSeverity.Warning); } }

        [Export(typeof(EditorFormatDefinition)), Name(Info), UserVisible(true)]
        internal sealed class InfoFormat : EditorFormatDefinition
        { public InfoFormat() { DisplayName = "Error Lens - Info"; ForegroundColor = Default(ErrorLensSeverity.Info); } }
    }

    internal sealed class ErrorLensAdornment
    {
        private const double Gap = 24;
        private const byte TintAlpha = 0x22;
        private readonly IWpfTextView view;
        private readonly ITagAggregator<IErrorTag> errors;
        private readonly IEditorFormatMap formats;
        private readonly IAdornmentLayer tint;
        private readonly IAdornmentLayer messages;
        private readonly Dictionary<ErrorLensSeverity, (Brush Text, Brush Tint)> brushes = new Dictionary<ErrorLensSeverity, (Brush, Brush)>();
        private bool pending;
        private bool closed;

        internal ErrorLensAdornment(IWpfTextView view, ITagAggregator<IErrorTag> errors, IEditorFormatMap formats)
        {
            this.view = view; this.errors = errors; this.formats = formats;
            tint = view.GetAdornmentLayer(ErrorLensListener.TintLayer);
            messages = view.GetAdornmentLayer(ErrorLensListener.MessageLayer);
            view.LayoutChanged += LayoutChanged;
            view.Closed += Closed;
            errors.BatchedTagsChanged += TagsChanged;
            formats.FormatMappingChanged += FormatsChanged;
        }

        private void LayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) => Redraw();

        // Taggers (LSP, Roslyn) may raise changes off the UI thread; coalesce them into one redraw.
        private void TagsChanged(object? sender, BatchedTagsChangedEventArgs e) => Schedule();

        private void FormatsChanged(object? sender, FormatItemsEventArgs e) { brushes.Clear(); Schedule(); }

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
            tint.RemoveAllAdornments();
            messages.RemoveAllAdornments();
            var lines = view.TextViewLines;
            if (lines == null || lines.Count == 0) return;
            var snapshot = view.TextSnapshot;
            var visible = new SnapshotSpan(lines.FirstVisibleLine.Start, lines.LastVisibleLine.EndIncludingLineBreak);
            if (visible.Snapshot != snapshot) return;

            // Each diagnostic belongs to the snapshot line where it starts, even for multiline squiggles.
            var byLine = new SortedDictionary<int, List<ErrorLensEntry>>();
            foreach (var tag in errors.GetTags(visible))
            {
                var severity = ErrorLens.Classify(tag.Tag.ErrorType);
                if (severity == ErrorLensSeverity.None) continue;
                var span = tag.Span.GetSpans(snapshot).FirstOrDefault();
                if (span.Snapshot == null) continue;
                var message = DiagnosticText(tag.Tag.ToolTipContent);
                if (string.IsNullOrWhiteSpace(message)) continue;
                int number = span.Start.GetContainingLine().LineNumber;
                if (!byLine.TryGetValue(number, out var list)) byLine[number] = list = new List<ErrorLensEntry>();
                list.Add(new ErrorLensEntry(severity, message));
            }

            var properties = view.FormattedLineSource?.DefaultTextProperties;
            foreach (var pair in byLine)
            {
                var text = ErrorLens.Summary(pair.Value, out var severity, out var tooltip);
                if (text == null) continue;
                var line = snapshot.GetLineFromLineNumber(pair.Key);
                // With word wrap a snapshot line spans several view lines: tint all, label the last.
                var first = lines.GetTextViewLineContainingBufferPosition(line.Start);
                var last = lines.GetTextViewLineContainingBufferPosition(line.End);
                if (first == null || last == null || last.VisibilityState == VisibilityState.Hidden) continue;
                var (textBrush, tintBrush) = Brushes(severity);

                var band = new Rectangle
                {
                    Width = Math.Max(view.ViewportWidth, view.MaxTextRightCoordinate) + view.ViewportWidth,
                    Height = Math.Max(0, last.Bottom - first.Top), Fill = tintBrush, IsHitTestVisible = false
                };
                Canvas.SetLeft(band, 0); Canvas.SetTop(band, first.Top);
                tint.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, band, null);

                var label = new TextBlock
                {
                    Text = Marker(severity) + " " + text, ToolTip = tooltip, Foreground = textBrush,
                    FontStyle = FontStyles.Italic, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(240, view.ViewportRight - last.TextRight - Gap - 8)
                };
                if (properties != null)
                {
                    label.FontFamily = properties.Typeface.FontFamily;
                    label.FontSize = properties.FontRenderingEmSize;
                }
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, last.TextRight + Gap);
                Canvas.SetTop(label, last.TextTop + Math.Max(0, (last.TextHeight - label.DesiredSize.Height) / 2));
                messages.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, label, null);
            }
        }

        private static string Marker(ErrorLensSeverity severity) =>
            severity == ErrorLensSeverity.Error ? "✖" : severity == ErrorLensSeverity.Warning ? "⚠" : "ℹ";

        private (Brush Text, Brush Tint) Brushes(ErrorLensSeverity severity)
        {
            if (brushes.TryGetValue(severity, out var cached)) return cached;
            var color = ErrorLensFormats.Default(severity);
            var map = formats.GetProperties(ErrorLensFormats.Name(severity));
            if (map.Contains(EditorFormatDefinition.ForegroundColorId) && map[EditorFormatDefinition.ForegroundColorId] is Color custom) color = custom;
            else if (map.Contains(EditorFormatDefinition.ForegroundBrushId) && map[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush brush) color = brush.Color;
            var text = new SolidColorBrush(color); text.Freeze();
            var band = new SolidColorBrush(Color.FromArgb(TintAlpha, color.R, color.G, color.B)); band.Freeze();
            return brushes[severity] = (text, band);
        }

        internal static string DiagnosticText(object? content)
        {
            if (content is string text) return text;
            if (content is TextBlock block) return block.Text;
            if (content is ClassifiedTextElement classified) return string.Concat(classified.Runs.Select(x => x.Text));
            if (content is ContainerElement container) return string.Join(" ", container.Elements.Select(DiagnosticText).Where(x => x.Length > 0));
            return content?.ToString() is string other && !other.StartsWith("System.", StringComparison.Ordinal) && !other.StartsWith("Microsoft.", StringComparison.Ordinal) ? other : "";
        }

        private void Closed(object? sender, EventArgs e)
        {
            if (closed) return; closed = true;
            view.LayoutChanged -= LayoutChanged; view.Closed -= Closed;
            errors.BatchedTagsChanged -= TagsChanged; errors.Dispose();
            formats.FormatMappingChanged -= FormatsChanged;
        }
    }
}
