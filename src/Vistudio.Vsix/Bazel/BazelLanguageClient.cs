using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Vistudio.Core;
using Vistudio.Core.Bazel;

namespace Vistudio.Bazel
{
    /// <summary>
    /// Starlark completion, hover, navigation and diagnostics from a user-installed starpls or bazel-lsp. Vistudio's own
    /// completion (<see cref="BazelCompletionSource"/>) and checks (<see cref="BazelCheckSession"/>) work without it.
    /// </summary>
    [Export(typeof(ILanguageClient)), Export(typeof(BazelLanguageClient))]
    [ContentType(BazelContentTypes.Starlark), PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class BazelLanguageClient : StdioLanguageClient
    {
        private static readonly Guid PaneId = new Guid("a6f3c1d2-4b5e-4f70-9a81-2b3c4d5e6f71");

        /// <summary>Raised after Tools &gt; Options &gt; Bazel is saved or the workspace's <c>vistudio.bazel.*</c> keys change.</summary>
        public static event EventHandler? SettingsChanged;

        internal static void OnSettingsChanged() => SettingsChanged?.Invoke(null, EventArgs.Empty);

        /// <summary>The server is running and initialized; Vistudio's completion then leaves names to it.</summary>
        internal static bool ServerRunning { get; private set; }

        [ImportingConstructor]
        public BazelLanguageClient([Import(typeof(SVsServiceProvider))] IServiceProvider services, WorkspaceSettingsMonitor workspaceSettings)
            : base(new LanguageEnvironment(services, workspaceSettings, PaneId, "Bazel"))
        {
            SettingsChanged += (_, __) => { ServerRunning = false; RestartAsync().Forget(); };
        }

        /// <summary>The "Bazel" Output pane, also used by the checks and buildifier.</summary>
        internal LanguageEnvironment Output => (LanguageEnvironment)Environment;

        public override string Name => "Bazel (Vistudio, Starlark language server)";
        public override IEnumerable<string> FilesToWatch => new[] { "**/BUILD", "**/BUILD.bazel", "**/*.bzl", "**/MODULE.bazel", "**/WORKSPACE", "**/WORKSPACE.bazel" };
        protected override string ServerLabel => "Starlark language server";
        protected override string InitializeFailedMessage => "The Starlark language server failed to initialize. Check the Bazel Output pane and Tools > Options > Bazel.";

        protected override bool CanStart()
        {
            var settings = BazelSettings.Load();
            if (BazelLanguageServer.Find(settings) != null) return true;
            Environment.Log(string.IsNullOrWhiteSpace(settings.LanguageServerPath)
                ? "Starlark language server: neither starpls nor bazel-lsp was found on PATH or in %USERPROFILE%\\go\\bin. Vistudio's own completion (files, folders, labels, rules, attributes) and checks still work; install starpls for hover and Go To Definition."
                : "Starlark language server: the configured server does not exist: " + settings.LanguageServerPath);
            return false;
        }

        protected override string DefaultRoot => WorkspaceSettings.CurrentRoot ?? Path.GetTempPath();

        protected override LanguageServerLaunch CreateLaunch(string root)
        {
            var settings = BazelSettings.Load();
            var server = BazelLanguageServer.Find(settings) ?? throw new FileNotFoundException("No Starlark language server was found; see Tools > Options > Bazel.");
            // A Bazel workspace root above the opened folder is the server's root, so labels resolve.
            var workspace = BazelWorkspace.FindRoot(root) ?? root;
            return new LanguageServerLaunch(BazelLanguageServer.Create(server, settings, workspace), "Starting " + Path.GetFileName(server) + " in " + workspace,
                parameters => LspInitialize.WithRoot(parameters, workspace));
        }

        protected override void OnInitialized() => ServerRunning = true;
    }
}
