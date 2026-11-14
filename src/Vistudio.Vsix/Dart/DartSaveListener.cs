using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>Hot reload on save: a saved .dart file reloads the running Flutter app (flutter run) and Flutter debug sessions of its package.</summary>
    // Matched on "text" plus the file name, like the Zig listener: the `dart` content type can be assigned after the view opens.
    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class DartSaveListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents = null!;
        [Import] internal DartRunService Run = null!;
        [Import] internal DartEnvironment Environment = null!;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var buffer = view.TextDataModel.DocumentBuffer;
            if (!Documents.TryGetTextDocument(buffer, out var document) || !DartProject.IsDartFile(document.FilePath)) return;
            // One subscription per document, however many views show it; removed with the last view.
            var views = buffer.Properties.GetOrCreateSingletonProperty(typeof(DartSaveListener), () =>
            {
                document.FileActionOccurred += Saved;
                return new ViewCount();
            });
            views.Count++;
            view.Closed += (_, __) =>
            {
                if (--views.Count > 0) return;
                document.FileActionOccurred -= Saved;
                buffer.Properties.RemoveProperty(typeof(DartSaveListener));
            };
        }

        private sealed class ViewCount { internal int Count; }

        private void Saved(object? sender, TextDocumentFileActionEventArgs e)
        {
            if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) == 0 || !DartProject.IsDartFile(e.FilePath)) return;
            if (!DartSettings.Load().HotReloadOnSave) return;
            Run.OnDartFileSaved(e.FilePath);
            var root = DartProject.FindRoot(e.FilePath);
            _ = Task.Run(async () =>
            {
                try { await DartDebugSessions.ReloadAsync(false, "save", root).ConfigureAwait(false); }
                catch (Exception error) { Environment.Log("Hot reload: " + error.Message); }
            });
        }
    }
}
