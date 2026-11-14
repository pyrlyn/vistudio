using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Dart;
using Vistudio.Zig;

namespace Vistudio.Dart
{
    /// <summary>Where Dart Error List entries come from. The analysis server's own diagnostics go through the LSP client.</summary>
    internal enum DartErrorSource { Run, Test, Analyze }

    /// <summary>
    /// Dart entries in the Error List besides the analysis server's live diagnostics: compiler errors from run/debug
    /// ("Dart run"), failed tests ("Dart tests") and a full <c>dart analyze</c> ("Dart analyze"). Each publish replaces
    /// that source's entries. Must be used on the UI thread.
    /// </summary>
    [Export, PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class DartErrorList
    {
        private readonly IServiceProvider services;
        private readonly DartEnvironment environment;
        private readonly Dictionary<DartErrorSource, ErrorListProvider> providers = new Dictionary<DartErrorSource, ErrorListProvider>();

        [ImportingConstructor]
        public DartErrorList([Import(typeof(SVsServiceProvider))] IServiceProvider services, DartEnvironment environment)
        {
            this.services = services;
            this.environment = environment;
        }

        private ErrorListProvider Provider(DartErrorSource source)
        {
            if (providers.TryGetValue(source, out var provider)) return provider;
            provider = new ErrorListProvider(services)
            {
                ProviderGuid = source switch
                {
                    DartErrorSource.Run => new Guid("4a7c2e91-0b3d-4f58-a6e2-9c1d3b5f7a20"),
                    DartErrorSource.Test => new Guid("5b8d3fa2-1c4e-4069-b7f3-ad2e4c6a8b31"),
                    _ => new Guid("6c9e40b3-2d5f-417a-88a4-be3f5d7b9c42"),
                },
                ProviderName = source switch { DartErrorSource.Run => "Dart run", DartErrorSource.Test => "Dart tests", _ => "Dart analyze" },
            };
            providers[source] = provider;
            return provider;
        }

        /// <summary>Replaces the entries of <paramref name="source"/>; brings the Error List forward when there are errors.</summary>
        public void Publish(DartErrorSource source, IReadOnlyList<DartDiagnostic> diagnostics)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var provider = Provider(source);
            provider.SuspendRefresh();
            try
            {
                foreach (ErrorTask task in provider.Tasks) task.Navigate -= Navigate;
                provider.Tasks.Clear();
                foreach (var diagnostic in diagnostics) provider.Tasks.Add(CreateTask(diagnostic, source));
            }
            finally { provider.ResumeRefresh(); }
            if (diagnostics.Any(d => d.Severity == DartDiagnosticSeverity.Error)) provider.BringToFront();
        }

        public int Count(DartErrorSource source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return providers.TryGetValue(source, out var provider) ? provider.Tasks.Count : 0;
        }

        private ErrorTask CreateTask(DartDiagnostic diagnostic, DartErrorSource source)
        {
            var task = new ErrorTask
            {
                Category = source == DartErrorSource.Analyze ? TaskCategory.CodeSense : TaskCategory.BuildCompile,
                ErrorCategory = diagnostic.Severity switch
                {
                    DartDiagnosticSeverity.Error => TaskErrorCategory.Error,
                    DartDiagnosticSeverity.Warning => TaskErrorCategory.Warning,
                    _ => TaskErrorCategory.Message,
                },
                // Automation (EnvDTE ErrorItem.ErrorLevel) reads the priority, not the category.
                Priority = diagnostic.Severity switch
                {
                    DartDiagnosticSeverity.Error => TaskPriority.High,
                    DartDiagnosticSeverity.Warning => TaskPriority.Normal,
                    _ => TaskPriority.Low,
                },
                Text = diagnostic.Code.Length == 0 ? diagnostic.Message : diagnostic.Message + " (" + diagnostic.Code + ")",
                Document = diagnostic.HasLocation ? diagnostic.File : "",
                Line = diagnostic.HasLocation ? diagnostic.Line : 0,
                Column = diagnostic.HasLocation ? Math.Max(0, diagnostic.Column) : 0,
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
            catch (Exception e) { environment.Log("Could not navigate to the Dart diagnostic: " + e.Message); }
        }
    }
}
