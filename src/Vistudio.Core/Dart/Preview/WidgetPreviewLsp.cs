using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>One preview reported by the Dart analysis server (<c>dart/textDocument/getFlutterWidgetPreviews</c>).</summary>
    public sealed class LspWidgetPreview
    {
        public LspWidgetPreview(string functionName, int line, int character, string? scriptUri, string? annotation, bool hasError, bool dependencyHasErrors, bool isBuilder, bool isMultiPreview)
        {
            FunctionName = functionName; Line = line; Character = character; ScriptUri = scriptUri; Annotation = annotation;
            HasError = hasError; DependencyHasErrors = dependencyHasErrors; IsBuilder = isBuilder; IsMultiPreview = isMultiPreview;
        }
        public string FunctionName { get; }
        /// <summary>0-based position of the previewed declaration.</summary>
        public int Line { get; }
        public int Character { get; }
        public string? ScriptUri { get; }
        /// <summary>The annotation source as the server reports it, for example <c>Preview(name: 'Card')</c>.</summary>
        public string? Annotation { get; }
        public bool HasError { get; }
        public bool DependencyHasErrors { get; }
        public bool IsBuilder { get; }
        public bool IsMultiPreview { get; }
    }

    /// <summary>A class symbol from <c>textDocument/documentSymbol</c>.</summary>
    public sealed class LspClassSymbol
    {
        public LspClassSymbol(string name, int line, int character) { Name = name; Line = line; Character = character; }
        public string Name { get; }
        /// <summary>0-based position of the class name (the symbol's selection range).</summary>
        public int Line { get; }
        public int Character { get; }
    }

    /// <summary>
    /// Reads the analysis server's answers that widget preview discovery uses: the Flutter widget preview requests the
    /// previewer itself relies on, document symbols (class positions) and type hierarchy supertypes (indirect widget
    /// subclasses). Unknown shapes give empty results instead of exceptions.
    /// </summary>
    public static class WidgetPreviewLsp
    {
        /// <summary>The per-file request the previewer uses through DTD; the IDE's server answers it over LSP too.</summary>
        public const string FilePreviewsMethod = "dart/textDocument/getFlutterWidgetPreviews";
        public const string WorkspacePreviewsMethod = "dart/workspace/getFlutterWidgetPreviews";

        /// <summary>Parses <c>{ previews: [ { functionName, position: {line, character}, ... } ], scriptUris, namespaces }</c>.</summary>
        public static IReadOnlyList<LspWidgetPreview> ParsePreviews(JToken? result)
        {
            // Through DTD the payload is wrapped once more: { type, result: { previews ... } }.
            if (result is JObject wrapper && wrapper["previews"] == null && wrapper["result"] is JObject inner) result = inner;
            if (!(result is JObject root) || !(root["previews"] is JArray previews)) return Array.Empty<LspWidgetPreview>();
            var list = new List<LspWidgetPreview>();
            foreach (var item in previews.OfType<JObject>())
            {
                var name = (string?)item["functionName"];
                if (string.IsNullOrEmpty(name)) continue;
                var position = item["position"] as JObject;
                list.Add(new LspWidgetPreview(name!, Int(position?["line"]), Int(position?["character"]), (string?)item["scriptUri"],
                    item["previewAnnotation"]?.Type == JTokenType.String ? (string?)item["previewAnnotation"] : item["previewAnnotation"]?.ToString(Newtonsoft.Json.Formatting.None),
                    Bool(item["hasError"]), Bool(item["dependencyHasErrors"]), Bool(item["isBuilder"]), Bool(item["isMultiPreview"])));
            }
            return list;
        }

        /// <summary>Class symbols (kind 5) from a DocumentSymbol tree or a SymbolInformation list.</summary>
        public static IReadOnlyList<LspClassSymbol> ParseClasses(JToken? result)
        {
            var list = new List<LspClassSymbol>();
            void Visit(JToken token)
            {
                if (!(token is JObject symbol)) return;
                if (Int(symbol["kind"]) == 5 && symbol["name"]?.Type == JTokenType.String)
                {
                    var range = symbol["selectionRange"] ?? symbol["range"] ?? symbol["location"]?["range"];
                    list.Add(new LspClassSymbol((string)symbol["name"]!, Int(range?["start"]?["line"]), Int(range?["start"]?["character"])));
                }
                if (symbol["children"] is JArray children) foreach (var child in children) Visit(child);
            }
            if (result is JArray array) foreach (var item in array) Visit(item);
            return list;
        }

        /// <summary>
        /// True when a supertype item from <c>typeHierarchy/supertypes</c> is Flutter's <c>StatelessWidget</c> or
        /// <c>StatefulWidget</c> (declared in package:flutter's framework.dart).
        /// </summary>
        public static bool IsFrameworkWidget(JToken? item)
        {
            if (!(item is JObject type)) return false;
            var name = (string?)type["name"];
            if (name != "StatelessWidget" && name != "StatefulWidget") return false;
            var uri = (string?)type["uri"] ?? "";
            return uri.IndexOf("flutter", StringComparison.OrdinalIgnoreCase) >= 0 && uri.EndsWith("framework.dart", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The <c>textDocument/prepareTypeHierarchy</c> parameters for a 0-based position.</summary>
        public static JObject Position(string uri, int line, int character) => new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri },
            ["position"] = new JObject { ["line"] = line, ["character"] = character },
        };

        /// <summary>
        /// Merges server previews (exact, includes custom MultiPreview annotations) into the scanner's targets: a server
        /// preview without a scanned target on its line becomes one. Server-reported errors are kept as problems.
        /// </summary>
        public static IReadOnlyList<WidgetPreviewTarget> Merge(IReadOnlyList<WidgetPreviewTarget> scanned, IReadOnlyList<LspWidgetPreview> server, string text)
        {
            if (server.Count == 0) return scanned;
            var result = scanned.ToList();
            var lineStarts = LineStarts(text);
            foreach (var preview in server)
            {
                var existing = result.FirstOrDefault(t => t.Name == preview.FunctionName)
                    ?? result.FirstOrDefault(t => WidgetPreviewScanner.LineOf(text, t.NameStart) == preview.Line);
                if (existing != null)
                {
                    if (preview.HasError && existing.Problem == null) existing.Problem = "The analysis server reports an error in this preview.";
                    continue;
                }
                if (preview.Line < 0 || preview.Line >= lineStarts.Count) continue;
                var offset = Math.Min(text.Length, lineStarts[preview.Line] + Math.Max(0, preview.Character));
                var annotation = WidgetPreviewScanner.ParseAnnotation("@" + (preview.Annotation ?? "Preview()"));
                annotation.IsMultiPreview = preview.IsMultiPreview;
                result.Add(new WidgetPreviewTarget
                {
                    Kind = preview.FunctionName.Contains('.') ? PreviewTargetKind.StaticMethod : PreviewTargetKind.Function,
                    Name = preview.FunctionName, Line = preview.Line, AnnotationStart = offset, NameStart = offset, NameEnd = offset,
                    Annotation = annotation, Problem = preview.HasError ? "The analysis server reports an error in this preview." : null,
                });
            }
            return result.OrderBy(t => t.AnnotationStart).ToList();
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') starts.Add(i + 1);
                else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) starts.Add(i + 1);
            }
            return starts;
        }

        private static int Int(JToken? token) => token != null && token.Type == JTokenType.Integer ? (int)token : -1;
        private static bool Bool(JToken? token) => token != null && token.Type == JTokenType.Boolean && (bool)token;
    }
}
