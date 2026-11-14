using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Zig;

namespace Vistudio.Zig
{
    /// <summary>
    /// Zig entries in the Error List: "Zig ast-check" (per document, replaced on every check) and "Zig build"
    /// (replaced by every build command). Must be used on the UI thread.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class ZigErrorList
    {
        private readonly IServiceProvider services;
        private readonly ZigEnvironment environment;
        private ErrorListProvider? astCheck;
        private ErrorListProvider? build;
        private readonly Dictionary<string, List<ErrorTask>> byFile = new Dictionary<string, List<ErrorTask>>(StringComparer.OrdinalIgnoreCase);

        [ImportingConstructor]
        public ZigErrorList([Import(typeof(SVsServiceProvider))] IServiceProvider services, ZigEnvironment environment)
        {
            this.services = services;
            this.environment = environment;
        }

        private ErrorListProvider AstCheck => astCheck ??= new ErrorListProvider(services)
        {
            ProviderGuid = new Guid("b1e5f3a2-6c4d-4e8f-9a0b-1c2d3e4f5a61"),
            ProviderName = "Zig ast-check",
        };

        private ErrorListProvider Build => build ??= new ErrorListProvider(services)
        {
            ProviderGuid = new Guid("c2f6a4b3-7d5e-4f90-8b1c-2d3e4f5a6b72"),
            ProviderName = "Zig build",
        };

        /// <summary>Replaces the ast-check entries of one document.</summary>
        public void PublishAstCheck(string file, IReadOnlyList<ZigDiagnostic> diagnostics)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var provider = AstCheck;
            provider.SuspendRefresh();
            try
            {
                ClearFile(provider, file);
                if (diagnostics.Count == 0) return;
                var tasks = diagnostics.Select(d => CreateTask(d, TaskCategory.CodeSense, file)).ToList();
                foreach (var task in tasks) provider.Tasks.Add(task);
                byFile[file] = tasks;
            }
            finally { provider.ResumeRefresh(); }
        }

        public void ClearAstCheck(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (astCheck != null) ClearFile(astCheck, file);
        }

        private void ClearFile(ErrorListProvider provider, string file)
        {
            if (!byFile.TryGetValue(file, out var previous)) return;
            foreach (var task in previous) { task.Navigate -= Navigate; provider.Tasks.Remove(task); }
            byFile.Remove(file);
        }

        /// <summary>Replaces all build entries; shows the Error List when there are errors.</summary>
        public void PublishBuild(IReadOnlyList<ZigDiagnostic> diagnostics)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var provider = Build;
            provider.SuspendRefresh();
            try
            {
                foreach (ErrorTask task in provider.Tasks) task.Navigate -= Navigate;
                provider.Tasks.Clear();
                // AstGen errors (undeclared identifiers, syntax) are found by both ast-check and the build; list them once.
                foreach (var diagnostic in diagnostics.Where(d => !ShownByAstCheck(d)))
                    provider.Tasks.Add(CreateTask(diagnostic, TaskCategory.BuildCompile, null));
            }
            finally { provider.ResumeRefresh(); }
            if (diagnostics.Any(d => d.Severity == ZigDiagnosticSeverity.Error)) provider.BringToFront();
        }

        private bool ShownByAstCheck(ZigDiagnostic diagnostic) =>
            diagnostic.HasLocation && byFile.TryGetValue(diagnostic.File, out var tasks) &&
            tasks.Any(t => t.Line == diagnostic.Line && t.Column == diagnostic.Column && t.Text == diagnostic.Message);

        private ErrorTask CreateTask(ZigDiagnostic diagnostic, TaskCategory category, string? fallbackFile)
        {
            var task = new ErrorTask
            {
                Category = category,
                ErrorCategory = diagnostic.Severity switch
                {
                    ZigDiagnosticSeverity.Error => TaskErrorCategory.Error,
                    ZigDiagnosticSeverity.Warning => TaskErrorCategory.Warning,
                    _ => TaskErrorCategory.Message,
                },
                // Automation (EnvDTE ErrorItem.ErrorLevel) and some views read the priority, not the category.
                Priority = diagnostic.Severity switch
                {
                    ZigDiagnosticSeverity.Error => TaskPriority.High,
                    ZigDiagnosticSeverity.Warning => TaskPriority.Normal,
                    _ => TaskPriority.Low,
                },
                Text = diagnostic.Message,
                Document = diagnostic.HasLocation ? diagnostic.File : fallbackFile ?? "",
                Line = diagnostic.HasLocation ? diagnostic.Line : 0,
                Column = diagnostic.HasLocation ? diagnostic.Column : 0,
                CanDelete = false,
            };
            task.Navigate += Navigate;
            return task;
        }

        private void Navigate(object sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(sender is ErrorTask task) || string.IsNullOrEmpty(task.Document)) return;
            try { ZigNavigation.Open(services, task.Document, task.Line, task.Column); }
            catch (Exception e) { environment.Log("Could not navigate to Zig diagnostic: " + e.Message); }
        }
    }

    internal static class ZigNavigation
    {
        /// <summary>Opens a file and puts the caret at a zero-based line and column, clamped to the current text.</summary>
        internal static void Open(IServiceProvider services, string path, int line, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(path)) throw new FileNotFoundException("The file no longer exists.", path);
            VsShellUtilities.OpenDocument(services, path, VSConstants.LOGVIEWID_Code, out _, out _, out var frame, out var view);
            if (view == null) throw new InvalidOperationException("No text editor is available for " + path);
            ErrorHandler.ThrowOnFailure(frame.Show());
            ErrorHandler.ThrowOnFailure(view.GetBuffer(out var buffer));
            ErrorHandler.ThrowOnFailure(buffer.GetLineCount(out var count));
            var row = Math.Max(0, Math.Min(line, count - 1));
            ErrorHandler.ThrowOnFailure(buffer.GetLengthOfLine(row, out var length));
            var col = Math.Max(0, Math.Min(column, length));
            ErrorHandler.ThrowOnFailure(view.SetCaretPos(row, col));
            ErrorHandler.ThrowOnFailure(view.SetSelection(row, col, row, col));
            ErrorHandler.ThrowOnFailure(view.CenterLines(row, 1));
        }
    }
}
