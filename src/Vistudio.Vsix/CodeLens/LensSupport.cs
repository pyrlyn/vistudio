using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Vistudio.Core.CodeLens;

namespace Vistudio
{
    /// <summary>The LSP position of a lens whose tagged range ends at the declaration name (Zig, Rust, WAT lenses).</summary>
    internal readonly struct LensPosition
    {
        private LensPosition(string path, string uri, int line, int character)
        { Path = path; Uri = uri; Line = line; Character = character; }
        internal string Path { get; }
        internal string Uri { get; }
        /// <summary>0-based line and UTF-16 character of the name's last character (inside <c>@"..."</c> or <c>$"..."</c> quotes).</summary>
        internal int Line { get; }
        internal int Character { get; }

        internal static LensPosition Of(TextRange range)
        {
            var document = range.Document;
            var text = document.Text.CopyToString();
            int start = range.Start, end = range.End;
            if (end > start + 1 && end <= text.Length && text[end - 1] == '"') end--; // quoted name: Zig @"x", WAT $"x"
            var (line, character) = CodeLensElement.NamePosition(text, start, end);
            var path = document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.ToString();
            var uri = document.Uri.IsFile ? new Uri(path).AbsoluteUri : document.Uri.ToString();
            return new LensPosition(path, uri, line, character);
        }
    }

    internal static class LensCommands
    {
        /// <summary>Puts the caret on the declaration name and runs a Visual Studio command there (the language server answers it).</summary>
        internal static async Task ExecuteAtAsync(LensPosition position, string command, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, position.Path, Guid.Empty, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            if (view == null) return;
            view.SetCaretPos(position.Line, position.Character);
            view.SendExplicitFocus();
            var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            dte.ExecuteCommand(command);
        }
    }

    /// <summary>A lens whose label depends on the language server (reference counts).</summary>
    internal interface IRefreshableLens
    {
        /// <summary>Asks Visual Studio for a new label; throws <see cref="ObjectDisposedException"/> after the lens is gone.</summary>
        void Refresh();
    }

    /// <summary>
    /// Server-backed lenses of one language: counts are workspace-wide, so they are asked again after edits (debounced),
    /// when the server becomes ready, and in steps after the server finished loading the project.
    /// </summary>
    internal sealed class LensRefresh
    {
        private readonly object gate = new object();
        private readonly HashSet<IRefreshableLens> lenses = new HashSet<IRefreshableLens>();
        private CancellationTokenSource? pending;
        private bool waiting;

        /// <summary>Raised by <see cref="InvalidateAll"/> after the lenses, for taggers whose elements come from the server.</summary>
        internal event Action? Invalidated;

        internal void Register(IRefreshableLens lens) { lock (gate) lenses.Add(lens); }
        internal void Unregister(IRefreshableLens lens) { lock (gate) lenses.Remove(lens); }

        internal void Schedule(int delayMilliseconds = 1500)
        {
            CancellationTokenSource source;
            lock (gate) { pending?.Cancel(); pending = source = new CancellationTokenSource(); }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delayMilliseconds, source.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                InvalidateAll();
            });
        }

        internal void InSteps(IEnumerable<int> seconds)
        {
            foreach (var delay in seconds)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                    InvalidateAll();
                });
            }
        }

        /// <summary>Polls the client once a second (up to 10 minutes) until its server is ready, then refreshes; one wait at a time.</summary>
        internal void WaitForServer(Func<Task<StdioLanguageClient>> clientAsync)
        {
            lock (gate) { if (waiting) return; waiting = true; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await clientAsync().ConfigureAwait(false);
                    for (var i = 0; i < 600 && !client.IsServerReady; i++) await Task.Delay(1000).ConfigureAwait(false);
                }
                catch (Exception) { }
                finally { lock (gate) waiting = false; }
                InvalidateAll();
            });
        }

        internal void InvalidateAll()
        {
            IRefreshableLens[] current;
            lock (gate) current = lenses.ToArray();
            foreach (var lens in current)
            {
                try { lens.Refresh(); }
                catch (ObjectDisposedException) { Unregister(lens); }
            }
            try { Invalidated?.Invoke(); }
            catch (Exception) { }
        }
    }
}
