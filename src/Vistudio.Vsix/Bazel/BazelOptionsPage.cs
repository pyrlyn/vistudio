using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Vistudio.Core.Bazel;

namespace Vistudio.Bazel
{
    /// <summary>Tools &gt; Options &gt; Bazel &gt; General. Persists to <see cref="BazelSettings"/>' file; saving restarts the Starlark server and rechecks open files.</summary>
    [ComVisible(true)]
    [Guid("5c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5")]
    public sealed class BazelOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private BazelSettings settings = BazelSettings.LoadGlobal();

        [Category("Validation"), DisplayName("Vistudio checks"), Description("Check BUILD, MODULE.bazel, WORKSPACE and .bzl files: Starlark syntax, load() statements, duplicate targets, invalid labels, missing files, packages and repositories. Results are squiggles and Error List entries (\"Bazel\").")]
        public bool Validation { get => settings.Validation; set => settings.Validation = value; }
        [Category("Validation"), DisplayName("Check while typing"), Description("Run Vistudio's checks after typing pauses, not only on open and save.")]
        public bool ValidateWhileTyping { get => settings.ValidateWhileTyping; set => settings.ValidateWhileTyping = value; }
        [Category("Validation"), DisplayName("buildifier lint"), Description("Run buildifier --lint=warn on open and save (the current text through stdin).")]
        public bool BuildifierLint { get => settings.BuildifierLint; set => settings.BuildifierLint = value; }
        [Category("Validation"), DisplayName("Report formatting"), Description("Show a suggestion when buildifier would reformat the file.")]
        public bool ReportFormatting { get => settings.ReportFormatting; set => settings.ReportFormatting = value; }
        [Category("Validation"), DisplayName("buildifier path"), Description("buildifier.exe (or its folder). Empty searches PATH, then %USERPROFILE%\\go\\bin.")]
        public string BuildifierPath { get => settings.BuildifierPath; set => settings.BuildifierPath = value; }
        [Category("Completion"), DisplayName("Vistudio completion"), Description("Files and folders in srcs, hdrs, data and glob(); //package, :target and @repository labels; load() files and symbols; visibility; rules and attributes (left to the language server while one runs).")]
        public bool Completion { get => settings.Completion; set => settings.Completion = value; }
        [Category("Language server"), DisplayName("Server path"), Description("starpls.exe or bazel-lsp.exe (or the folder). Empty searches PATH for starpls, then bazel-lsp. Adds hover, Go To Definition and the server's own diagnostics.")]
        public string LanguageServerPath { get => settings.LanguageServerPath; set => settings.LanguageServerPath = value; }
        [Category("Language server"), DisplayName("Arguments"), Description("Extra server arguments. starpls gets its 'server' subcommand automatically.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Build cache"), DisplayName("Disk cache folder"), Description("Folder passed as --disk_cache when a project's Build cache is on. Empty uses %LOCALAPPDATA%\\Vistudio\\bazel-disk-cache.")]
        public string DiskCachePath { get => settings.DiskCachePath; set => settings.DiskCachePath = value; }

        public override void LoadSettingsFromStorage() => settings = BazelSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            BazelLanguageClient.OnSettingsChanged();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try { settings.Validate(); }
            catch (ArgumentException error)
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error.Message, "Bazel settings", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            base.OnApply(e);
        }

        public override void ResetSettings() => settings = new BazelSettings();
    }
}
