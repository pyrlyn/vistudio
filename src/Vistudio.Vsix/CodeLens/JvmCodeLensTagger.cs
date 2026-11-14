using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Vistudio.Core.CodeLens;

namespace Vistudio
{
    /// <summary>
    /// In-process VisualStudio.Extensibility entry point. It hosts only the CodeLens parts below; every other
    /// feature of this VSIX stays on the VSSDK package and MEF.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class VistudioExtension : Extension
    {
        public override ExtensionConfiguration ExtensionConfiguration => new() { RequiresInProcessHosting = true };
    }

    /// <summary>
    /// Marks Kotlin and Java declarations as CodeLens code elements. Visual Studio's own taggers do this only for
    /// Roslyn languages; the lens providers in this folder then attach references, implementations and Run/Debug.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class JvmCodeLensTaggerProvider : ExtensionPart, ITextViewTaggerProvider<CodeLensTag>, ITextViewChangedListener
    {
        private readonly object gate = new object();
        private readonly Dictionary<Uri, List<JvmCodeLensTagger>> taggers = new Dictionary<Uri, List<JvmCodeLensTagger>>();

        // Kotlin/Java views are matched by file name in code: Visual Studio assigns the `kotlin`/`java` content
        // type after the view opens, and neither the document-type nor a glob filter reliably attached the tagger.
        public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
        {
            AppliesTo = new[] { DocumentFilter.FromDocumentType("text") },
        };

        public Task<TextViewTaggerBase<CodeLensTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
        {
            var tagger = new JvmCodeLensTagger(this, textView.Document.Uri);
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) taggers[tagger.Uri] = list = new List<JvmCodeLensTagger>();
                list.Add(tagger);
            }
            return Task.FromResult<TextViewTaggerBase<CodeLensTag>>(tagger);
        }

        internal void Remove(JvmCodeLensTagger tagger)
        {
            lock (gate)
            {
                if (!taggers.TryGetValue(tagger.Uri, out var list)) return;
                list.Remove(tagger);
                if (list.Count == 0) taggers.Remove(tagger.Uri);
            }
        }

        public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
        {
            if (args.Edits.Count == 0) return;
            var uri = args.AfterTextView.Document.Uri;
            // The parts apply to every text view: edits in other files (C#, JSON, ...) do not change JVM counts.
            var path = uri.IsFile ? uri.LocalPath : uri.ToString();
            var script = GradleScriptScanner.IsGradleScript(path);
            if (!script && !CodeDeclarationScanner.IsSupportedFile(path)) return;
            JvmCodeLensTagger[] current;
            lock (gate) current = taggers.TryGetValue(uri, out var list) ? list.ToArray() : Array.Empty<JvmCodeLensTagger>();
            foreach (var tagger in current) await tagger.DocumentChangedAsync(args.AfterTextView.Document, cancellationToken);
            if (script) return; // Gradle task lenses do not depend on the language servers
            // Edits change reference counts anywhere in the workspace: refresh the counts after the server catches up.
            JvmCodeLensRefresh.Schedule();
        }
    }

    internal sealed class JvmCodeLensTagger : TextViewTagger<CodeLensTag>
    {
        private readonly JvmCodeLensTaggerProvider provider;

        public JvmCodeLensTagger(JvmCodeLensTaggerProvider provider, Uri uri)
        {
            this.provider = provider;
            Uri = uri;
            Initialize(provider, uri);
        }

        internal Uri Uri { get; }

        protected override Task OnRequestTagsAsync(NormalizedTextRangeCollection requestedRanges, bool recalculateAll, CancellationToken cancellationToken)
        {
            if (requestedRanges.Count == 0) return Task.CompletedTask;
            return UpdateAllAsync(requestedRanges.First().Document, cancellationToken);
        }

        protected override Task OnTextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken) => Task.CompletedTask;

        internal Task DocumentChangedAsync(ITextDocumentSnapshot document, CancellationToken cancellationToken) => UpdateAllAsync(document, cancellationToken);

        /// <summary>Scans the whole document: declarations are cheap to find and a lens moves with every edit above it.</summary>
        private async Task UpdateAllAsync(ITextDocumentSnapshot document, CancellationToken cancellationToken)
        {
            var path = document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.ToString();
            if (GradleScriptScanner.IsGradleScript(path))
            {
                await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, GradleTags(document), cancellationToken);
                return;
            }
            if (!CodeDeclarationScanner.IsSupportedFile(path)) return;
            var text = document.Text.CopyToString();
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            var declarations = CodeDeclarationScanner.Scan(path, text);
            var identifiers = CodeLensElement.Identifiers(declarations);
            for (var i = 0; i < declarations.Count; i++)
            {
                var declaration = declarations[i];
                var tag = new CodeLensTag(JvmCodeElement.Kind(declaration))
                {
                    Description = declaration.QualifiedName,
                    UniqueIdentifier = identifiers[i],
                };
                tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                    new TextRange(document, declaration.StartOffset, declaration.EndOffset - declaration.StartOffset), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
            }
            await UpdateTagsAsync(new[] { new TextRange(document, 0, document.Length) }, tags, cancellationToken);
        }

        /// <summary>Tasks declared in a Kotlin Gradle script get a "Run task" lens.</summary>
        private static List<TaggedTrackingTextRange<CodeLensTag>> GradleTags(ITextDocumentSnapshot document)
        {
            var tasks = GradleScriptScanner.Scan(document.Text.CopyToString());
            var identifiers = CodeLensElement.Identifiers(tasks);
            var tags = new List<TaggedTrackingTextRange<CodeLensTag>>();
            for (var i = 0; i < tasks.Count; i++)
            {
                var tag = new CodeLensTag(CodeElementKind.KnownValues.Method) { Description = "Gradle task " + tasks[i].Name, UniqueIdentifier = identifiers[i] };
                tags.Add(new TaggedTrackingTextRange<CodeLensTag>(
                    new TextRange(document, tasks[i].StartOffset, tasks[i].EndOffset - tasks[i].StartOffset), TextRangeTrackingMode.ExtendForwardAndBackward, tag));
            }
            return tags;
        }

        public override void Dispose()
        {
            provider.Remove(this);
            base.Dispose();
        }
    }

    /// <summary>A code element tagged by <see cref="JvmCodeLensTagger"/>; identity format in <see cref="CodeLensElement"/>.</summary>
    internal sealed class JvmCodeElement
    {
        private JvmCodeElement(bool java, CodeLensElement element, string path)
        {
            Java = java; CanBeImplemented = element.CanBeImplemented; IsMain = element.IsMain; Element = element; Path = path;
        }

        internal bool Java { get; }
        /// <summary>The parsed identity: kind, traits and qualified name (or Gradle task name).</summary>
        internal CodeLensElement Element { get; }
        /// <summary>Local path of the document, or "".</summary>
        internal string Path { get; }
        internal bool CanBeImplemented { get; }
        internal bool IsMain { get; }

        internal static CodeElementKind Kind(CodeDeclaration declaration) => declaration.Kind switch
        {
            CodeDeclarationKind.Interface => CodeElementKind.KnownValues.Interface,
            CodeDeclarationKind.Enum => CodeElementKind.KnownValues.Enum,
            CodeDeclarationKind.Class or CodeDeclarationKind.Object => CodeElementKind.KnownValues.Class,
            CodeDeclarationKind.Function => CodeElementKind.KnownValues.Method,
            CodeDeclarationKind.Property => CodeElementKind.KnownValues.Property,
            CodeDeclarationKind.Constructor => CodeElementKind.KnownValues.Constructor,
            _ => CodeElementKind.KnownValues.Member,
        };

        internal static JvmCodeElement? TryParse(CodeElement element, CodeElementContext context)
        {
            var parsed = CodeLensElement.TryParse(element.UniqueIdentifier);
            if (parsed == null) return null;
            var path = context.Range.Document.Uri.IsFile ? context.Range.Document.Uri.LocalPath : "";
            return new JvmCodeElement(path.EndsWith(".java", StringComparison.OrdinalIgnoreCase), parsed, path);
        }
    }
}
