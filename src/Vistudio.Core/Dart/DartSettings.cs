namespace Vistudio.Core.Dart
{
    /// <summary>
    /// Dart and Flutter settings (Tools &gt; Options &gt; Dart). Stored in their own file, independent of the Kotlin and
    /// Zig settings, and overridden per window by <c>vistudio.dart.*</c> keys in the workspace's <c>.vs\settings.json</c>.
    /// </summary>
    public sealed class DartSettings : StoredSettings<DartSettings>
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

        public DartSettings() : base("dart-settings.xml", WorkspaceSettings.DartPrefix) { }

        /// <summary>Throws <see cref="ArgumentException"/> for values Tools &gt; Options must reject.</summary>
        public override void Validate()
        {
            DartArguments.Split(FlutterRunArguments);
            DartArguments.Split(ProgramArguments);
            DartArguments.Split(TestArguments);
            DartArguments.Split(LanguageServerArguments);
            if (FlutterDevice.Length > 0) DartArguments.ValidateDevice(FlutterDevice);
        }
    }
}
