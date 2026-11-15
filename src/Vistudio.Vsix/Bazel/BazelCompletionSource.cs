using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Bazel;
using Vistudio.Core.Languages;

namespace Vistudio.Bazel
{
    /// <summary>Vistudio's own Bazel completion in BUILD, MODULE.bazel, WORKSPACE and .bzl editors.</summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("Vistudio Bazel completion"), ContentType(BazelContentTypes.Starlark)]
    internal sealed class BazelCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;

        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new BazelCompletionSource(Documents));
    }

    /// <summary>
    /// Files and folders, <c>//package</c>, <c>:target</c> and <c>@repository</c> labels, load() files and symbols,
    /// visibility values, rules and attributes (<see cref="BazelCompletion"/>). Opens by itself on <c>"</c>, <c>/</c>,
    /// <c>:</c>, <c>@</c> and on letters; the workspace is read on a background thread.
    /// </summary>
    internal sealed class BazelCompletionSource : IAsyncCompletionSource
    {
        private const string DescriptionKey = "Vistudio.Bazel.Description";
        private static readonly ImageElement TargetIcon = new ImageElement(new ImageId(LanguageCatalog.IconGuid, LanguageIcons.BazelFile));
        private readonly ITextDocumentFactoryService documents;

        public BazelCompletionSource(ITextDocumentFactoryService documents) { this.documents = documents; }

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            if (!BazelSettings.Load().Completion || !documents.TryGetTextDocument(triggerLocation.Snapshot.TextBuffer, out _))
                return CompletionStartData.DoesNotParticipateInCompletion;
            if (trigger.Reason == CompletionTriggerReason.Insertion)
            {
                var c = trigger.Character;
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '"' || c == '\'' || c == '/' || c == ':' || c == '@' || c == '.' || c == '-'))
                    return CompletionStartData.DoesNotParticipateInCompletion;
            }
            var snapshot = triggerLocation.Snapshot;
            var start = BazelCompletion.ApplicableStart(snapshot.GetText(), triggerLocation.Position, names: !BazelLanguageClient.ServerRunning);
            if (start == null) return CompletionStartData.DoesNotParticipateInCompletion;
            return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(snapshot, start.Value, triggerLocation.Position - start.Value));
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan, CancellationToken token)
        {
            if (!documents.TryGetTextDocument(triggerLocation.Snapshot.TextBuffer, out var document)) return CompletionContext.Empty;
            var path = document.FilePath;
            var text = triggerLocation.Snapshot.GetText();
            var caret = triggerLocation.Position;
            var names = !BazelLanguageClient.ServerRunning;
            var result = await Task.Run(() => BazelCompletion.Complete(path, text, caret, names), token).ConfigureAwait(false);
            if (result == null) return CompletionContext.Empty;
            var items = result.Items.Select((item, index) =>
            {
                var completion = new CompletionItem(item.Label, this, Icon(item.Kind), ImmutableArray<CompletionFilter>.Empty, Suffix(item.Kind),
                    item.InsertText, index.ToString("D4"), item.Label, ImmutableArray<ImageElement>.Empty);
                completion.Properties.AddProperty(DescriptionKey, item.Description);
                return completion;
            }).ToImmutableArray();
            return new CompletionContext(items);
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token) =>
            Task.FromResult<object>(item.Properties.TryGetProperty(DescriptionKey, out string description) ? description : "");

        private static string Suffix(BazelCompletionKind kind) => kind switch
        {
            BazelCompletionKind.Rule => "rule",
            BazelCompletionKind.Package => "package",
            BazelCompletionKind.Target => "target",
            BazelCompletionKind.Repository => "repository",
            _ => "",
        };

        private static ImageElement Icon(BazelCompletionKind kind) => kind switch
        {
            BazelCompletionKind.Rule => new ImageElement(KnownMonikers.Method.ToImageId()),
            BazelCompletionKind.Function => new ImageElement(KnownMonikers.MethodPublic.ToImageId()),
            BazelCompletionKind.Attribute => new ImageElement(KnownMonikers.Property.ToImageId()),
            BazelCompletionKind.Keyword => new ImageElement(KnownMonikers.IntellisenseKeyword.ToImageId()),
            BazelCompletionKind.Symbol => new ImageElement(KnownMonikers.Field.ToImageId()),
            BazelCompletionKind.File => new ImageElement(KnownMonikers.Document.ToImageId()),
            BazelCompletionKind.Folder => new ImageElement(KnownMonikers.FolderClosed.ToImageId()),
            BazelCompletionKind.Package => new ImageElement(KnownMonikers.Package.ToImageId()),
            BazelCompletionKind.Repository => new ImageElement(KnownMonikers.Library.ToImageId()),
            BazelCompletionKind.Value => new ImageElement(KnownMonikers.Constant.ToImageId()),
            _ => TargetIcon,
        };
    }

    /// <summary>
    /// Typed characters never commit a Bazel item: paths and labels go on with '/', ':' and '.', which would otherwise
    /// commit the selected folder or package and then insert the character again. Tab and Enter commit.
    /// </summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name("Vistudio Bazel commit"), ContentType(BazelContentTypes.Starlark)]
    internal sealed class BazelCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        private static readonly BazelCommitManager Instance = new BazelCommitManager();

        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) => Instance;

        private sealed class BazelCommitManager : IAsyncCompletionCommitManager
        {
            public IEnumerable<char> PotentialCommitCharacters => Array.Empty<char>();

            public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

            public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token) =>
                CommitResult.Unhandled;
        }
    }
}
