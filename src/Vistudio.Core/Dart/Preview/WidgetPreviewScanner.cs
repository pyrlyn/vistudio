using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart.Preview
{
    public enum PreviewTargetKind { Function, StaticMethod, Constructor, Factory, InstanceMethod }

    /// <summary>The arguments of a <c>@Preview(...)</c> annotation that the preview window shows and respects.</summary>
    public sealed class PreviewAnnotationInfo
    {
        public string Text { get; internal set; } = "";
        public string? Name { get; internal set; }
        public string? Group { get; internal set; }
        /// <summary>Width and height from <c>size: Size(w, h)</c>, <c>Size.fromWidth</c>, <c>Size.fromHeight</c> or <c>Size.square</c>; null when unconstrained.</summary>
        public double? Width { get; internal set; }
        public double? Height { get; internal set; }
        public double? TextScaleFactor { get; internal set; }
        /// <summary><c>light</c> or <c>dark</c> from <c>brightness: Brightness.x</c>.</summary>
        public string? Brightness { get; internal set; }
        public bool HasTheme { get; internal set; }
        public bool HasLocalizations { get; internal set; }
        public bool HasWrapper { get; internal set; }
        /// <summary>A custom <c>MultiPreview</c> subclass annotation (several previews from one annotation).</summary>
        public bool IsMultiPreview { get; internal set; }

        /// <summary>A short description for tooltips: "name 'Dark card', 390x844, dark, text x1.3".</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Name != null) parts.Add("name '" + Name + "'");
            if (Group != null) parts.Add("group '" + Group + "'");
            if (Width != null || Height != null) parts.Add((Width?.ToString("0.#", CultureInfo.InvariantCulture) ?? "auto") + "×" + (Height?.ToString("0.#", CultureInfo.InvariantCulture) ?? "auto"));
            if (Brightness != null) parts.Add(Brightness);
            if (TextScaleFactor != null) parts.Add("text ×" + TextScaleFactor.Value.ToString("0.0#", CultureInfo.InvariantCulture));
            if (HasTheme) parts.Add("theme");
            if (HasLocalizations) parts.Add("localizations");
            if (HasWrapper) parts.Add("wrapper");
            if (IsMultiPreview) parts.Add("multi-preview");
            return parts.Count == 0 ? "default settings" : string.Join(", ", parts);
        }
    }

    /// <summary>A declaration annotated with <c>@Preview</c> (or a <c>MultiPreview</c> subclass declared in the same file).</summary>
    public sealed class WidgetPreviewTarget
    {
        public PreviewTargetKind Kind { get; internal set; }
        /// <summary>As the analysis server names it: <c>preview</c>, <c>MyWidget.previewStatic</c>, <c>MyWidget.preview</c>, <c>MyWidget</c>.</summary>
        public string Name { get; internal set; } = "";
        public string? ClassName { get; internal set; }
        /// <summary>0-based line of the annotation.</summary>
        public int Line { get; internal set; }
        /// <summary>Offset of the <c>@</c>; the CodeLens sits on this line.</summary>
        public int AnnotationStart { get; internal set; }
        public int NameStart { get; internal set; }
        public int NameEnd { get; internal set; }
        public PreviewAnnotationInfo Annotation { get; internal set; } = new PreviewAnnotationInfo();
        /// <summary>Why the previewer will reject it (private, instance method, required parameters), or null.</summary>
        public string? Problem { get; internal set; }
    }

    /// <summary>A class that extends <c>StatelessWidget</c> or <c>StatefulWidget</c> (directly, through a class of the same file or a well-known base).</summary>
    public sealed class WidgetClassInfo
    {
        public string Name { get; internal set; } = "";
        public string? SuperClass { get; internal set; }
        public int Line { get; internal set; }
        /// <summary>First non-blank character of the header line (where CodeLens attaches).</summary>
        public int StartOffset { get; internal set; }
        public int NameStart { get; internal set; }
        public int NameEnd { get; internal set; }
        public int BodyOpen { get; internal set; }
        public int BodyClose { get; internal set; }
        public bool IsAbstract { get; internal set; }
        public bool IsPrivate => Name.StartsWith("_", StringComparison.Ordinal);
        /// <summary>True: a widget; null: extends a class this file does not declare (the analysis server decides).</summary>
        public bool? IsWidget { get; internal set; }
        /// <summary>A no-argument instance creation, such as <c>const GreetingCard()</c> or <c>Card.preview()</c>; null when every constructor needs arguments.</summary>
        public string? Construction { get; internal set; }
        /// <summary>Why the class cannot be previewed without writing a preview function, or null.</summary>
        public string? Problem { get; internal set; }
        /// <summary>A <c>@Preview</c> constructor, factory or static method inside the class already previews it.</summary>
        public bool HasPreview { get; internal set; }
    }

    /// <summary>What "Preview Widget" does for a file and caret position.</summary>
    public sealed class PreviewChoice
    {
        private PreviewChoice(string? wrap, string? message) { WrapWidget = wrap; Message = message; }
        /// <summary>A widget without <c>@Preview</c> to show through the temporary host project; null: the file's own previews.</summary>
        public string? WrapWidget { get; }
        /// <summary>Set when there is nothing to preview (the reason); null otherwise.</summary>
        public string? Message { get; }
        public bool HasPreview => Message == null;

        /// <summary>
        /// The widget class around <paramref name="caret"/> without <c>@Preview</c> is wrapped; a class with previews, or a
        /// caret outside classes in a file with previews, shows the file's previews; a file with a single previewable widget
        /// and no previews wraps it; otherwise the message explains what to do.
        /// </summary>
        public static PreviewChoice For(WidgetPreviewFile file, int caret)
        {
            var around = file.Widgets.Where(w => w.IsWidget != false && w.StartOffset <= caret && caret <= w.BodyClose).OrderByDescending(w => w.StartOffset).FirstOrDefault();
            if (around != null)
            {
                if (around.HasPreview) return new PreviewChoice(null, null);
                if (around.Problem == null && around.Construction != null) return new PreviewChoice(around.Name, null);
                if (!file.Targets.Any(t => t.Problem == null))
                    return new PreviewChoice(null, (around.Problem ?? "'" + around.Name + "' cannot be created without arguments.") + " Add a @Preview function that builds it.");
            }
            if (file.Targets.Any(t => t.Problem == null)) return new PreviewChoice(null, null);
            var candidates = file.Widgets.Where(w => w.IsWidget == true && w.Problem == null && w.Construction != null).ToList();
            if (candidates.Count == 1) return new PreviewChoice(candidates[0].Name, null);
            return new PreviewChoice(null, candidates.Count == 0
                ? "This file has no @Preview declarations and no widget class that can be created without arguments."
                : "This file has no @Preview declarations. Put the caret in the widget class to preview (" + string.Join(", ", candidates.Select(c => c.Name)) + "), or add @Preview with the light bulb on a class.");
        }
    }

    public sealed class WidgetPreviewFile
    {
        public WidgetPreviewFile(bool hasImport, string? prefix, IReadOnlyList<WidgetPreviewTarget> targets, IReadOnlyList<WidgetClassInfo> widgets)
        { HasPreviewImport = hasImport; PreviewPrefix = prefix; Targets = targets; Widgets = widgets; }
        /// <summary>The file imports <c>package:flutter/widget_previews.dart</c>.</summary>
        public bool HasPreviewImport { get; }
        /// <summary>The import prefix (<c>import '...widget_previews.dart' as wp;</c>), or null.</summary>
        public string? PreviewPrefix { get; }
        public IReadOnlyList<WidgetPreviewTarget> Targets { get; }
        public IReadOnlyList<WidgetClassInfo> Widgets { get; }
    }

    /// <summary>
    /// Lexical discovery of widget previews in one Dart file, used when the analysis server is not available and to place
    /// CodeLens: <c>@Preview</c> annotations (from <c>package:flutter/widget_previews.dart</c>) on top-level functions,
    /// static methods, constructors and factories, and widget classes. Comments and strings are masked first
    /// (<see cref="DartSourceScanner.Mask"/>) so they never produce results.
    /// </summary>
    public static class WidgetPreviewScanner
    {
        public const string PreviewLibrary = "package:flutter/widget_previews.dart";

        /// <summary>Bases from popular packages that are themselves Stateless/StatefulWidget subclasses.</summary>
        public static readonly IReadOnlyCollection<string> KnownWidgetBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "StatelessWidget", "StatefulWidget", "HookWidget", "StatefulHookWidget", "ConsumerWidget", "ConsumerStatefulWidget",
            "HookConsumerWidget", "StatefulHookConsumerWidget",
        };

        private static readonly Regex Import = new Regex(@"^\s*import\s+(?<q>['""])(?<uri>[^'""]+)\k<q>(?<rest>[^;]*);", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex AsPrefix = new Regex(@"\bas\s+(?<prefix>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);
        private static readonly Regex Annotation = new Regex(@"@\s*(?:(?<prefix>[A-Za-z_$][\w$]*)\s*\.\s*)?(?<name>[A-Za-z_$][\w$]*)\s*(?<args>\()?", RegexOptions.CultureInvariant);
        private static readonly Regex ClassKeyword = new Regex(@"(?<![\w$])(?<modifiers>(?:(?:abstract|base|final|interface|sealed|mixin)\s+)*)class\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingName = new Regex(@"(?<a>[A-Za-z_$][\w$]*)(?:\s*\.\s*(?<b>[A-Za-z_$][\w$]*))?\s*$", RegexOptions.CultureInvariant);

        public static WidgetPreviewFile Scan(string text)
        {
            var masked = DartSourceScanner.Mask(text);
            var (hasImport, prefix) = FindImport(text, masked);
            var classes = FindClasses(masked);
            var multiPreviews = new HashSet<string>(classes.Where(c => c.Super == "MultiPreview").Select(c => c.Name), StringComparer.Ordinal);
            var targets = FindTargets(text, masked, classes, hasImport, prefix, multiPreviews);
            var widgets = FindWidgets(text, masked, classes, targets);
            return new WidgetPreviewFile(hasImport, prefix, targets, widgets);
        }

        private static (bool, string?) FindImport(string text, string masked)
        {
            foreach (Match match in Import.Matches(text))
            {
                var keyword = text.IndexOf("import", match.Index, StringComparison.Ordinal);
                if (keyword < 0 || string.CompareOrdinal(masked, keyword, "import", 0, 6) != 0) continue; // inside a comment
                if (match.Groups["uri"].Value != PreviewLibrary) continue;
                var alias = AsPrefix.Match(match.Groups["rest"].Value);
                return (true, alias.Success ? alias.Groups["prefix"].Value : null);
            }
            return (false, null);
        }

        private sealed class ClassRange
        {
            public string Name = "";
            public string? Super;
            public bool IsAbstract;
            public int HeaderStart, NameStart, NameEnd, Open, Close;
        }

        private static List<ClassRange> FindClasses(string masked)
        {
            var result = new List<ClassRange>();
            foreach (Match match in ClassKeyword.Matches(masked))
            {
                var name = match.Groups["name"];
                var i = SkipBlank(masked, name.Index + name.Length);
                if (i < masked.Length && masked[i] == '<') i = SkipBlank(masked, SkipAngles(masked, i));
                string? super = null;
                if (IsWord(masked, i, "extends"))
                {
                    i = SkipBlank(masked, i + 7);
                    var start = i;
                    while (i < masked.Length && (IsIdentifier(masked[i]) || masked[i] == '.')) i++;
                    super = masked.Substring(start, i - start);
                    var dot = super.LastIndexOf('.');
                    if (dot >= 0) super = super.Substring(dot + 1); // prefixed import: w.StatelessWidget
                }
                var open = masked.IndexOfAny(new[] { '{', ';' }, i);
                if (open < 0 || masked[open] != '{') continue; // class Foo = Bar with Mixin;
                var close = Matching(masked, open, '{', '}');
                result.Add(new ClassRange
                {
                    Name = name.Value, Super = super, IsAbstract = match.Groups["modifiers"].Value.Contains("abstract"),
                    HeaderStart = match.Index, NameStart = name.Index, NameEnd = name.Index + name.Length, Open = open, Close = close < 0 ? masked.Length - 1 : close,
                });
            }
            return result;
        }

        private static List<WidgetPreviewTarget> FindTargets(string text, string masked, List<ClassRange> classes, bool hasImport, string? prefix, HashSet<string> multiPreviews)
        {
            var result = new List<WidgetPreviewTarget>();
            var depth = Depths(masked);
            foreach (Match match in Annotation.Matches(masked))
            {
                var name = match.Groups["name"].Value;
                var annotationPrefix = match.Groups["prefix"].Success ? match.Groups["prefix"].Value : null;
                var isPreview = name == "Preview" && hasImport && annotationPrefix == prefix;
                // Without the import the code does not compile, but the user is likely adding it: still recognize @Preview(.
                if (name == "Preview" && !hasImport && annotationPrefix == null) isPreview = true;
                var isMulti = annotationPrefix == null && multiPreviews.Contains(name);
                if (!(isPreview || isMulti) || !match.Groups["args"].Success) continue;
                var open = match.Groups["args"].Index;
                var close = Matching(masked, open, '(', ')');
                if (close < 0) continue;
                var info = ParseAnnotation(text.Substring(match.Index, close + 1 - match.Index));
                info.IsMultiPreview = isMulti;
                var target = ParseDeclaration(text, masked, close + 1, classes, depth);
                if (target == null) continue;
                target.Line = LineOf(text, match.Index);
                target.AnnotationStart = match.Index;
                target.Annotation = info;
                result.Add(target);
            }
            return result;
        }

        /// <summary>The declaration after an annotation ending at <paramref name="from"/>: skips further annotations, stops at <c>(</c>.</summary>
        private static WidgetPreviewTarget? ParseDeclaration(string text, string masked, int from, List<ClassRange> classes, int[] depth)
        {
            var i = SkipBlank(masked, from);
            while (i < masked.Length && masked[i] == '@')
            {
                var other = Annotation.Match(masked, i);
                if (!other.Success || other.Index != i) return null;
                i = other.Index + other.Length;
                if (other.Groups["args"].Success)
                {
                    var end = Matching(masked, other.Groups["args"].Index, '(', ')');
                    if (end < 0) return null;
                    i = end + 1;
                }
                i = SkipBlank(masked, i);
            }
            var paren = -1;
            for (var j = i; j < masked.Length; j++)
            {
                var c = masked[j];
                if (c == '(') { paren = j; break; }
                if (c == ';' || c == '{' || c == '}' || c == '=') return null; // a field, variable or class: not a preview target
            }
            if (paren < 0) return null;
            var header = masked.Substring(i, paren - i);
            // Generic functions: Widget preview<T>() - drop the type parameters.
            var headerForName = header.TrimEnd();
            if (headerForName.EndsWith(">", StringComparison.Ordinal))
            {
                var lt = headerForName.LastIndexOf('<');
                if (lt > 0) headerForName = headerForName.Substring(0, lt);
            }
            var names = TrailingName.Match(headerForName);
            if (!names.Success) return null;
            var a = names.Groups["a"].Value;
            var b = names.Groups["b"].Success ? names.Groups["b"].Value : null;
            var words = header.Substring(0, names.Index).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var owner = classes.Where(c => c.Open < i && i < c.Close && depth[i] == depth[c.Open] + 1).OrderByDescending(c => c.Open).FirstOrDefault();
            var target = new WidgetPreviewTarget
            {
                NameStart = i + names.Index,
                NameEnd = i + names.Index + names.Length,
            };
            var closeParen = Matching(masked, paren, '(', ')');
            var parameters = closeParen < 0 ? "" : masked.Substring(paren + 1, closeParen - paren - 1);
            if (owner == null)
            {
                if (b != null || depth[i] != 0) return null;
                target.Kind = PreviewTargetKind.Function;
                target.Name = a;
            }
            else
            {
                target.ClassName = owner.Name;
                if (words.Contains("factory"))
                {
                    target.Kind = PreviewTargetKind.Factory;
                    target.Name = b == null ? owner.Name : owner.Name + "." + b;
                }
                else if (a == owner.Name && words.All(w => w == "const" || w == "external"))
                {
                    target.Kind = PreviewTargetKind.Constructor;
                    target.Name = b == null ? owner.Name : owner.Name + "." + b;
                }
                else
                {
                    if (b != null) return null;
                    target.Kind = words.Contains("static") ? PreviewTargetKind.StaticMethod : PreviewTargetKind.InstanceMethod;
                    target.Name = owner.Name + "." + a;
                }
            }
            target.Problem = TargetProblem(target, owner, parameters);
            return target;
        }

        private static string? TargetProblem(WidgetPreviewTarget target, ClassRange? owner, string parameters)
        {
            if (target.Kind == PreviewTargetKind.InstanceMethod) return "@Preview methods must be static.";
            if (owner != null && owner.Name.StartsWith("_", StringComparison.Ordinal)) return "@Preview needs a public class; '" + owner.Name + "' is private.";
            var simple = target.Name.Substring(target.Name.LastIndexOf('.') + 1);
            if (simple.StartsWith("_", StringComparison.Ordinal)) return "@Preview needs a public declaration; '" + target.Name + "' is private.";
            if (HasRequiredParameters(parameters)) return "@Preview declarations cannot have required parameters.";
            if (owner != null && owner.IsAbstract && (target.Kind == PreviewTargetKind.Constructor)) return "@Preview cannot construct the abstract class '" + owner.Name + "'.";
            return null;
        }

        /// <summary>True when a parameter list (masked, without parentheses) has positional parameters outside <c>[]</c> or <c>required</c> named ones.</summary>
        public static bool HasRequiredParameters(string parameters)
        {
            var text = parameters.Trim();
            if (text.Length == 0) return false;
            var optional = text.IndexOfAny(new[] { '{', '[' });
            var positional = (optional < 0 ? text : text.Substring(0, optional)).Trim().TrimEnd(',').Trim();
            if (positional.Length > 0) return true;
            return Regex.IsMatch(text, @"(?<![\w$])required(?![\w$])");
        }

        private static List<WidgetClassInfo> FindWidgets(string text, string masked, List<ClassRange> classes, List<WidgetPreviewTarget> targets)
        {
            var byName = classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            bool? IsWidget(ClassRange c, int guard)
            {
                if (c.Super == null || guard > 16) return false;
                if (KnownWidgetBases.Contains(c.Super)) return true;
                if (byName.TryGetValue(c.Super, out var parent)) return IsWidget(parent, guard + 1);
                // State<T>, ChangeNotifier, Object...: well-known non-widgets; anything else needs the analysis server.
                return NonWidgetBases.Contains(c.Super) ? false : (bool?)null;
            }
            var result = new List<WidgetClassInfo>();
            foreach (var c in classes)
            {
                var widget = IsWidget(c, 0);
                if (widget == false) continue;
                var info = new WidgetClassInfo
                {
                    Name = c.Name, SuperClass = c.Super, Line = LineOf(text, c.HeaderStart), StartOffset = LineStart(text, c.HeaderStart),
                    NameStart = c.NameStart, NameEnd = c.NameEnd, BodyOpen = c.Open, BodyClose = c.Close, IsAbstract = c.IsAbstract, IsWidget = widget,
                    HasPreview = targets.Any(t => t.ClassName == c.Name && t.Problem == null),
                };
                (info.Construction, info.Problem) = Construction(masked, c);
                if (c.IsAbstract) info.Problem = "'" + c.Name + "' is abstract.";
                else if (info.IsPrivate) info.Problem = "'" + c.Name + "' is private; previews must be public.";
                result.Add(info);
            }
            return result;
        }

        private static readonly HashSet<string> NonWidgetBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "State", "Object", "ChangeNotifier", "ValueNotifier", "MultiPreview", "Preview", "Exception", "Error", "CustomPainter", "CustomClipper",
            "InheritedWidget", "InheritedNotifier", "InheritedModel", "RenderObject", "RenderBox", "ThemeExtension", "Equatable",
        };

        private static readonly Regex ConstructorPattern = new Regex(@"(?<![\w$.])(?<const>const\s+)?(?<factory>factory\s+)?(?<name>[A-Za-z_$][\w$]*)(?:\s*\.\s*(?<named>[A-Za-z_$][\w$]*))?\s*\(", RegexOptions.CultureInvariant);

        /// <summary>A no-argument creation of <paramref name="c"/>: the unnamed constructor first, then named ones; implicit default when none is declared.</summary>
        private static (string?, string?) Construction(string masked, ClassRange c)
        {
            var depth = 0;
            var declared = new List<(string? Named, bool Const, bool Required)>();
            for (var i = c.Open + 1; i < c.Close; i++)
            {
                var ch = masked[i];
                if (ch == '{') { depth++; continue; }
                if (ch == '}') { depth--; continue; }
                if (depth != 0 || !IsIdentifierStart(ch) || (i > 0 && (IsIdentifier(masked[i - 1]) || masked[i - 1] == '.'))) continue;
                var match = ConstructorPattern.Match(masked, i);
                if (!match.Success || match.Index != i || match.Groups["name"].Value != c.Name) continue;
                // A member declaration starts after {, }, ; or an annotation's ')' - not inside an expression.
                var before = i - 1;
                while (before > c.Open && char.IsWhiteSpace(masked[before])) before--;
                if (!(masked[before] == '{' || masked[before] == '}' || masked[before] == ';' || masked[before] == ')' || IsAnnotationName(masked, before))) continue;
                var open = match.Index + match.Length - 1;
                var close = Matching(masked, open, '(', ')');
                if (close < 0) break;
                declared.Add((match.Groups["named"].Success ? match.Groups["named"].Value : null, match.Groups["const"].Success,
                    HasRequiredParameters(masked.Substring(open + 1, close - open - 1))));
                i = close;
            }
            if (declared.Count == 0) return (c.Name + "()", null);
            var usable = declared.Where(d => !d.Required && (d.Named == null || !d.Named.StartsWith("_", StringComparison.Ordinal)))
                .OrderBy(d => d.Named == null ? 0 : 1).FirstOrDefault();
            if (usable == default) return (null, "Every constructor of '" + c.Name + "' needs arguments; write a @Preview function that builds it with sample values.");
            return ((usable.Const ? "const " : "") + c.Name + (usable.Named == null ? "" : "." + usable.Named) + "()", null);
        }

        private static bool IsAnnotationName(string masked, int end)
        {
            // "@override" without parentheses right before the constructor.
            var start = end;
            while (start >= 0 && IsIdentifier(masked[start])) start--;
            return start >= 0 && start < end && masked[start] == '@';
        }

        private static readonly Regex NameArgument = new Regex(@"(?<![\w$])name\s*:\s*(?<q>['""])(?<v>(?:\\.|(?!\k<q>).)*)\k<q>", RegexOptions.CultureInvariant);
        private static readonly Regex GroupArgument = new Regex(@"(?<![\w$])group\s*:\s*(?<q>['""])(?<v>(?:\\.|(?!\k<q>).)*)\k<q>", RegexOptions.CultureInvariant);
        private static readonly Regex SizeArgument = new Regex(@"(?<![\w$])size\s*:\s*(?:const\s+)?Size\s*(?:\.\s*(?<ctor>fromWidth|fromHeight|square)\s*)?\(\s*(?<a>[\d.]+|double\.infinity)\s*(?:,\s*(?<b>[\d.]+|double\.infinity)\s*)?,?\s*\)", RegexOptions.CultureInvariant);
        private static readonly Regex ScaleArgument = new Regex(@"(?<![\w$])textScaleFactor\s*:\s*(?<v>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex BrightnessArgument = new Regex(@"(?<![\w$])brightness\s*:\s*(?:[A-Za-z_$][\w$]*\.)?Brightness\.(?<v>light|dark)", RegexOptions.CultureInvariant);

        /// <summary>Reads the constant arguments of <c>@Preview(...)</c> (the annotation's source text).</summary>
        public static PreviewAnnotationInfo ParseAnnotation(string annotation)
        {
            var info = new PreviewAnnotationInfo { Text = annotation.Trim() };
            var name = NameArgument.Match(annotation);
            if (name.Success) info.Name = Regex.Unescape(name.Groups["v"].Value.Replace("\\$", "$"));
            var group = GroupArgument.Match(annotation);
            if (group.Success) info.Group = group.Groups["v"].Value;
            var size = SizeArgument.Match(annotation);
            if (size.Success)
            {
                double? Value(Group g) => !g.Success || g.Value == "double.infinity" ? (double?)null
                    : double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
                switch (size.Groups["ctor"].Value)
                {
                    case "fromWidth": info.Width = Value(size.Groups["a"]); break;
                    case "fromHeight": info.Height = Value(size.Groups["a"]); break;
                    case "square": info.Width = info.Height = Value(size.Groups["a"]); break;
                    default: info.Width = Value(size.Groups["a"]); info.Height = Value(size.Groups["b"]); break;
                }
            }
            var scale = ScaleArgument.Match(annotation);
            if (scale.Success) info.TextScaleFactor = double.Parse(scale.Groups["v"].Value, CultureInfo.InvariantCulture);
            var brightness = BrightnessArgument.Match(annotation);
            if (brightness.Success) info.Brightness = brightness.Groups["v"].Value;
            info.HasTheme = Regex.IsMatch(annotation, @"(?<![\w$])theme\s*:");
            info.HasLocalizations = Regex.IsMatch(annotation, @"(?<![\w$])localizations\s*:");
            info.HasWrapper = Regex.IsMatch(annotation, @"(?<![\w$])wrapper\s*:");
            return info;
        }

        private static int[] Depths(string masked)
        {
            var depth = new int[masked.Length + 1];
            var current = 0;
            for (var i = 0; i < masked.Length; i++)
            {
                depth[i] = current;
                if (masked[i] == '{') current++;
                else if (masked[i] == '}' && current > 0) current--;
            }
            depth[masked.Length] = current;
            return depth;
        }

        private static int Matching(string masked, int open, char opening, char closing)
        {
            var level = 0;
            for (var i = open; i < masked.Length; i++)
            {
                if (masked[i] == opening) level++;
                else if (masked[i] == closing && --level == 0) return i;
            }
            return -1;
        }

        private static int SkipAngles(string masked, int open)
        {
            var level = 0;
            for (var i = open; i < masked.Length; i++)
            {
                if (masked[i] == '<') level++;
                else if (masked[i] == '>' && --level == 0) return i + 1;
                else if (masked[i] == '{' || masked[i] == ';') return i;
            }
            return masked.Length;
        }

        private static bool IsWord(string text, int i, string word) =>
            i + word.Length <= text.Length && string.CompareOrdinal(text, i, word, 0, word.Length) == 0 && (i + word.Length == text.Length || !IsIdentifier(text[i + word.Length]));

        private static int SkipBlank(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

        /// <summary>0-based line of an offset (LF, CRLF or CR line breaks).</summary>
        public static int LineOf(string text, int offset)
        {
            var line = 0;
            for (var i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\n') line++;
                else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) line++;
            }
            return line;
        }

        private static int LineStart(string text, int offset)
        {
            var start = offset;
            while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;
            while (start < offset && (text[start] == ' ' || text[start] == '\t')) start++;
            return start;
        }
    }
}
