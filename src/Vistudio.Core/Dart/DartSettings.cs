using System;

namespace Vistudio.Core.Dart
{
    /// <summary>
    /// Dart and Flutter settings (Tools &gt; Options &gt; Dart). Stored in their own file, independent of the Kotlin and
    /// Zig settings, and overridden per window by <c>vistudio.dart.*</c> keys in the workspace's <c>.vs\settings.json</c>.
    /// </summary>
    public sealed class DartSettings : StoredSettings<DartSettings>, Editor.IEditorSettings
    {
        /// <summary>Flutter SDK folder (contains <c>bin\flutter.bat</c>). Empty searches PATH. Its bundled Dart SDK is used when <see cref="DartSdkPath"/> is empty.</summary>
        public string FlutterSdkPath { get; set; } = "";
        /// <summary>Dart SDK folder (contains <c>bin\dart.exe</c>). Empty uses the Flutter SDK's Dart, then PATH.</summary>
        public string DartSdkPath { get; set; } = "";
        /// <summary>Flutter device id for Run, Debug and integration tests (<c>flutter devices</c>), for example <c>windows</c> or <c>chrome</c>.</summary>
        public string FlutterDevice { get; set; } = "windows";
        /// <summary>Hot reload a running Flutter app (run or debug) when a .dart file of its project is saved.</summary>
        public bool HotReloadOnSave { get; set; } = true;
        /// <summary>Extra arguments for <c>flutter run</c>, for example <c>--flavor dev</c> or <c>--release</c>.</summary>
        public string FlutterRunArguments { get; set; } = "";
        /// <summary>Program arguments for <c>dart run</c> and Dart debugging.</summary>
        public string ProgramArguments { get; set; } = "";
        /// <summary>Extra arguments for <c>dart test</c> / <c>flutter test</c>, for example <c>--tags fast</c>.</summary>
        public string TestArguments { get; set; } = "";
        /// <summary>Extra arguments for <c>dart language-server</c>, for example <c>--instrumentation-log-file=C:\temp\das.log</c>.</summary>
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>Start the Dart analysis server (completion, navigation, diagnostics) for .dart files.</summary>
        public bool LanguageServer { get; set; } = true;
        /// <summary>Stop in SDK and package sources while stepping (DAP <c>debugSdkLibraries</c> / <c>debugExternalPackageLibraries</c>).</summary>
        public bool DebugLibraries { get; set; }
        public bool CodeLens { get; set; } = true;

        // Editor display (IEditorSettings), the same settings Kotlin has under Tools > Options > Kotlin > Editor.
        public bool RainbowBraces { get; set; } = true;
        public ScrollBarMode ScrollBarMode { get; set; } = ScrollBarMode.Inherit;
        public ScrollBarMapWidth ScrollBarMapWidth { get; set; } = ScrollBarMapWidth.Medium;
        public bool ScrollBarMapPreview { get; set; } = true;
        public InlayHintsDisplay InlayHintsDisplay { get; set; } = InlayHintsDisplay.Always;

        /// <summary>Flutter widget previews: the Preview CodeLens, the Preview Widget commands and the Flutter Widget Preview window.</summary>
        public bool WidgetPreview { get; set; } = true;
        /// <summary>"Preview" CodeLens above <c>@Preview</c> functions and widget classes (needs <see cref="CodeLens"/> too).</summary>
        public bool WidgetPreviewCodeLens { get; set; } = true;
        /// <summary>When the previewer reloads: after a save (the previewer watches the files), or also after typing stops (the document is saved after the delay).</summary>
        public WidgetPreviewReloadTrigger WidgetPreviewReload { get; set; } = WidgetPreviewReloadTrigger.OnSave;
        /// <summary>Typing pause in milliseconds before an automatic save with <see cref="WidgetPreviewReloadTrigger.AfterDelay"/> (200-10000).</summary>
        public int WidgetPreviewReloadDelay { get; set; } = 1000;
        /// <summary>Device size presets, <c>Name=WIDTHxHEIGHT</c> in logical pixels, separated by <c>;</c>.</summary>
        public string WidgetPreviewDevices { get; set; } = Preview.PreviewConfigurations.DefaultDevices;
        /// <summary>Text scale factors offered by the preview window, separated by commas.</summary>
        public string WidgetPreviewTextScales { get; set; } = Preview.PreviewConfigurations.DefaultTextScales;
        /// <summary>Locales offered by the preview window (BCP 47 tags such as <c>en-US</c>), separated by commas.</summary>
        public string WidgetPreviewLocales { get; set; } = Preview.PreviewConfigurations.DefaultLocales;
        /// <summary>Run <c>flutter pub get</c> before starting the previewer when the package's dependencies are not resolved.</summary>
        public bool WidgetPreviewPubGet { get; set; } = true;
        /// <summary>Pass <c>--verbose</c> to the previewer and write its trace messages to the Dart Output pane.</summary>
        public bool WidgetPreviewVerbose { get; set; }

        public DartSettings() : base("dart-settings.xml", WorkspaceSettings.DartPrefix) { }

        /// <summary>Throws <see cref="ArgumentException"/> for values Tools &gt; Options must reject.</summary>
        public override void Validate()
        {
            DartArguments.Split(FlutterRunArguments);
            DartArguments.Split(ProgramArguments);
            DartArguments.Split(TestArguments);
            DartArguments.Split(LanguageServerArguments);
            if (FlutterDevice.Length > 0) DartArguments.ValidateDevice(FlutterDevice);
            if (WidgetPreviewReloadDelay < 200 || WidgetPreviewReloadDelay > 10000)
                throw new ArgumentException("Widget preview reload delay must be between 200 and 10000 milliseconds.");
            Preview.PreviewConfigurations.ParseDevices(WidgetPreviewDevices);
            Preview.PreviewConfigurations.ParseTextScales(WidgetPreviewTextScales);
            Preview.PreviewConfigurations.ParseLocales(WidgetPreviewLocales);
        }
    }

    /// <summary>What makes a running widget previewer reload.</summary>
    public enum WidgetPreviewReloadTrigger
    {
        /// <summary>Saving a file (the previewer watches the package on disk).</summary>
        OnSave,
        /// <summary>Also after typing stops: Vistudio saves the edited Dart file of a previewed package after the delay.</summary>
        AfterDelay,
    }
}
