using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>Tools &gt; Options &gt; Dart &gt; General. Persists to <see cref="DartSettings.FilePath"/>.</summary>
    [ComVisible(true)]
    [Guid("2f8b6d41-9a3e-4c7d-8b52-6e1f0a3c9d84")]
    public sealed class DartOptionsPage : DialogPage
    {
        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private DartSettings settings = DartSettings.LoadGlobal();

        [Category("SDK"), DisplayName("Flutter SDK folder"), Description("Folder of a user-installed Flutter SDK (contains bin\\flutter.bat). Empty searches PATH. Flutter packages (pubspec.yaml depends on sdk: flutter) run, test and debug through it, and its bundled Dart SDK is used when the Dart SDK folder is empty.")]
        public string FlutterSdkPath { get => settings.FlutterSdkPath; set => settings.FlutterSdkPath = value; }
        [Category("SDK"), DisplayName("Dart SDK folder"), Description("Folder of a Dart SDK (contains bin\\dart.exe). Empty uses the Flutter SDK's Dart, then dart.exe on PATH. Versions appear in the Dart Output pane.")]
        public string DartSdkPath { get => settings.DartSdkPath; set => settings.DartSdkPath = value; }
        [Category("Flutter"), DisplayName("Device"), Description("Flutter device id for Run, Debug and integration tests, for example windows, chrome or an emulator id ('Flutter: Select Device' lists them).")]
        public string FlutterDevice { get => settings.FlutterDevice; set => settings.FlutterDevice = value; }
        [Category("Flutter"), DisplayName("Hot reload on save"), Description("Hot reload a running Flutter app (Run or Debug) when a .dart file of its package is saved.")]
        public bool HotReloadOnSave { get => settings.HotReloadOnSave; set => settings.HotReloadOnSave = value; }
        [Category("Flutter"), DisplayName("flutter run arguments"), Description("Extra arguments for flutter run and Flutter debugging, for example --flavor dev. Double quotes group an argument; % is not allowed (flutter.bat runs through cmd.exe).")]
        public string FlutterRunArguments { get => settings.FlutterRunArguments; set => settings.FlutterRunArguments = value; }
        [Category("Run and test"), DisplayName("Program arguments"), Description("Arguments passed to Dart console programs (dart run and Dart debugging).")]
        public string ProgramArguments { get => settings.ProgramArguments; set => settings.ProgramArguments = value; }
        [Category("Run and test"), DisplayName("Test arguments"), Description("Extra arguments for dart test / flutter test, for example --tags fast.")]
        public string TestArguments { get => settings.TestArguments; set => settings.TestArguments = value; }
        [Category("Debugging"), DisplayName("Debug SDK and package libraries"), Description("Step into and stop in Dart SDK and pub package sources.")]
        public bool DebugLibraries { get => settings.DebugLibraries; set => settings.DebugLibraries = value; }
        [Category("Analysis server"), DisplayName("Enabled"), Description("Start the Dart analysis server (dart language-server) for completion, navigation, hover, diagnostics in the Error List and reference counts. Restarts when settings are saved.")]
        public bool LanguageServer { get => settings.LanguageServer; set => settings.LanguageServer = value; }
        [Category("Analysis server"), DisplayName("Arguments"), Description("Extra dart language-server arguments, for example --instrumentation-log-file=C:\\temp\\das.log.")]
        public string LanguageServerArguments { get => settings.LanguageServerArguments; set => settings.LanguageServerArguments = value; }
        [Category("Editor"), DisplayName("CodeLens"), Description("Run | Debug above main and tests, reference counts above functions and types. Reopen editors after changing.")]
        public bool CodeLens { get => settings.CodeLens; set => settings.CodeLens = value; }

        public override void LoadSettingsFromStorage() => settings = DartSettings.LoadGlobal();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            DartEnvironment.OnSettingsChanged();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            try { settings.Validate(); }
            catch (ArgumentException error)
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, error.Message, "Dart settings", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            base.OnApply(e);
        }

        public override void ResetSettings() => settings = new DartSettings();
    }
}
