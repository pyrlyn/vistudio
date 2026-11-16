using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Dart;

namespace Vistudio.Dart
{
    /// <summary>Tools &gt; Options &gt; Dart &gt; General. Persists to <see cref="DartSettings.FilePath"/>.</summary>
    [ComVisible(true)]
    [Guid(PageGuidString)]
    public sealed class DartOptionsPage : DialogPage
    {
        internal const string PageGuidString = "2f8b6d41-9a3e-4c7d-8b52-6e1f0a3c9d84";

        // Global defaults only; a workspace's .vs\settings.json overrides them per window and is never written here.
        private DartSettings settings = DartSettings.LoadGlobal();

        [Category("SDK"), DisplayName("Flutter SDK folder"), Description("Folder of a user-installed Flutter SDK (contains bin\\flutter.bat). Empty searches FLUTTER_ROOT, PATH, fvm, mise and asdf (project pins first) and standard folders such as C:\\src\\flutter; the SDK in use is logged in the Dart Output pane. Flutter packages (pubspec.yaml depends on sdk: flutter) run, test, debug and preview through it, and its bundled Dart SDK is used when the Dart SDK folder is empty.")]
        public string FlutterSdkPath { get => settings.FlutterSdkPath; set => settings.FlutterSdkPath = value; }
        [Category("SDK"), DisplayName("Flutter SDK in use"), Description("The Flutter SDK the current Flutter SDK folder setting resolves to and where it was found (read-only; this window's .vs\\settings.json can override the folder)."), ReadOnly(true)]
        public string FlutterSdkInUse
        {
            get
            {
                var lookup = DartToolchain.LocateFlutter(settings, Vistudio.Core.Dart.Preview.FlutterSdkEnvironment.Current, null);
                if (lookup.Location == null) return lookup.Error ?? "Not found";
                return lookup.Location.Root + " - Flutter " + (DartToolchain.ReadFlutterVersion(lookup.Location.Root) ?? "?") + " (" + lookup.Location.Detail + ")";
            }
        }
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

        [Category("Widget preview"), DisplayName("Enabled"), Description("Flutter widget previews (flutter widget-preview, Flutter 3.35+): the Preview CodeLens, Preview Widget commands and the Flutter Widget Preview window. Turning it off stops a running previewer.")]
        public bool WidgetPreview { get => settings.WidgetPreview; set => settings.WidgetPreview = value; }
        [Category("Widget preview"), DisplayName("CodeLens"), Description("Show Preview above @Preview declarations and widget classes (needs Editor > CodeLens). Reopen editors after changing.")]
        public bool WidgetPreviewCodeLens { get => settings.WidgetPreviewCodeLens; set => settings.WidgetPreviewCodeLens = value; }
        [Category("Widget preview"), DisplayName("Reload"), Description("OnSave: the previewer hot reloads when a file of the package is saved. AfterDelay: also after typing stops - the edited Dart file of the previewed package is saved after the delay below.")]
        public WidgetPreviewReloadTrigger WidgetPreviewReload { get => settings.WidgetPreviewReload; set => settings.WidgetPreviewReload = value; }
        [Category("Widget preview"), DisplayName("Reload delay (ms)"), Description("Typing pause before the automatic save with Reload = AfterDelay, 200-10000 milliseconds.")]
        public int WidgetPreviewReloadDelay { get => settings.WidgetPreviewReloadDelay; set => settings.WidgetPreviewReloadDelay = value; }
        [Category("Widget preview"), DisplayName("Device presets"), Description("Device sizes offered in the preview window, Name=WIDTHxHEIGHT in logical pixels separated by semicolons, for example Phone=390x844; Tablet=820x1180; Desktop=1440x900.")]
        public string WidgetPreviewDevices { get => settings.WidgetPreviewDevices; set => settings.WidgetPreviewDevices = value; }
        [Category("Widget preview"), DisplayName("Text scales"), Description("Text scale factors offered in the preview window, separated by commas, for example 1.0, 1.3, 2.0. @Preview(textScaleFactor: ...) takes precedence.")]
        public string WidgetPreviewTextScales { get => settings.WidgetPreviewTextScales; set => settings.WidgetPreviewTextScales = value; }
        [Category("Widget preview"), DisplayName("Locales"), Description("Locales offered in the preview window (language tags such as en-US, de-DE, ar), separated by commas. They become the page's platform locales; previews resolve them against their supported locales (@Preview(localizations: ...)).")]
        public string WidgetPreviewLocales { get => settings.WidgetPreviewLocales; set => settings.WidgetPreviewLocales = value; }
        [Category("Widget preview"), DisplayName("Run pub get"), Description("Run flutter pub get before starting the previewer when the package's dependencies are not resolved (progress in the status bar and the Dart Output pane).")]
        public bool WidgetPreviewPubGet { get => settings.WidgetPreviewPubGet; set => settings.WidgetPreviewPubGet = value; }
        [Category("Widget preview"), DisplayName("Verbose log"), Description("Pass --verbose to flutter widget-preview and write its trace messages to the Dart Output pane.")]
        public bool WidgetPreviewVerbose { get => settings.WidgetPreviewVerbose; set => settings.WidgetPreviewVerbose = value; }

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
