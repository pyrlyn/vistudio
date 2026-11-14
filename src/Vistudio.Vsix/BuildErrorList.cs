using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core;

namespace Vistudio
{
    internal sealed class BuildErrorList : IDisposable
    {
        private readonly IServiceProvider services;
        private readonly Action<string> log;
        private readonly ErrorListProvider provider;

        public BuildErrorList(IServiceProvider services, Action<string> log)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.services = services;
            this.log = log;
            provider = new ErrorListProvider(services)
            {
                ProviderGuid = new Guid("8eac51ba-7043-4979-9b87-b1a713b91a7a"),
                ProviderName = "Kotlin Gradle"
            };
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ErrorTask task in provider.Tasks) task.Navigate -= Navigate;
            provider.Tasks.Clear();
            KotlinDiagnosticStore.Publish(Array.Empty<BuildDiagnostic>());
        }

        public void Publish(IEnumerable<BuildDiagnostic> diagnostics, DateTime buildStartedUtc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var items = diagnostics.ToArray();
            provider.SuspendRefresh();
            try
            {
                Clear();
                foreach (var item in items)
                {
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.BuildCompile,
                        ErrorCategory = item.Severity == BuildDiagnosticSeverity.Error ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                        Text = item.Message, Document = item.File, Line = item.Line, Column = item.Column,
                        CanDelete = false
                    };
                    task.Navigate += Navigate;
                    provider.Tasks.Add(task);
                }
            }
            finally { provider.ResumeRefresh(); }
            KotlinDiagnosticStore.Publish(items, buildStartedUtc);
            if (provider.Tasks.Count > 0) provider.Show();
        }

        private void Navigate(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(sender is ErrorTask task) || string.IsNullOrEmpty(task.Document)) return;
            try
            {
                if (!File.Exists(task.Document)) throw new FileNotFoundException("Diagnostic source file no longer exists.", task.Document);
                VsShellUtilities.OpenDocument(services, task.Document, VSConstants.LOGVIEWID_Code,
                    out _, out _, out var frame, out var view);
                if (view == null) throw new InvalidOperationException("No text editor is available for " + task.Document);
                ErrorHandler.ThrowOnFailure(frame.Show());
                ErrorHandler.ThrowOnFailure(view.GetBuffer(out var buffer));
                ErrorHandler.ThrowOnFailure(buffer.GetLineCount(out var count));
                // The file may have changed since compilation. Clamp, without modifying the task.
                var line = Math.Max(0, Math.Min(task.Line, count - 1));
                ErrorHandler.ThrowOnFailure(buffer.GetLengthOfLine(line, out var length));
                var column = Math.Max(0, Math.Min(task.Column, length));
                ErrorHandler.ThrowOnFailure(view.SetCaretPos(line, column));
                ErrorHandler.ThrowOnFailure(view.SetSelection(line, column, line, column));
                ErrorHandler.ThrowOnFailure(view.CenterLines(line, 1));
            }
            catch (Exception e) { log("Could not navigate to Gradle diagnostic: " + e.Message); }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Clear();
            provider.Dispose();
        }
    }
}
