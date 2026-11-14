using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Languages;

namespace Vistudio.Languages
{
    /// <summary>
    /// Build/Rebuild/Clean run the project's command settings; F5/Ctrl+F5 start the program under the debugger that fits
    /// the language: the native Visual Studio engine for programs with PDBs (Rust, Zig, C, C++), the Dart/Flutter
    /// adapter, or the Kotlin/JVM session for Java. Languages without a debugger run without debugging and say so.
    /// </summary>
    internal sealed class LanguageProjectConfig : BuildableProjectConfig
    {
        private readonly LanguageProjectNode project;
        private readonly LanguagesPackage package;

        public LanguageProjectConfig(LanguageProjectNode project, LanguagesPackage package, string name, string platform) : base(package, name, platform)
        {
            this.project = project;
            this.package = package;
        }

        protected override void Log(string message) => package.Log(project.Language.DisplayName + ": " + message);

        // The tools track their own inputs (cargo, zig build, CMake, Gradle); Visual Studio always asks them, and they
        // return quickly when nothing changed.
        protected override bool IsUpToDate() => false;

        protected override async Task<bool> RunAsync(string operation, Action<string> mirror, CancellationToken token)
        {
            await package.SaveAllAsync();
            var success = await package.RunCommandsAsync(project.Metadata, operation, mirror, token);
            project.LastSuccessfulBuildUtc = success && operation != "clean" ? DateTime.UtcNow : (DateTime?)null;
            return success;
        }

        public override int DebugLaunch(uint grfLaunch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var noDebug = (grfLaunch & (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_NoDebug) != 0;
            var metadata = project.Metadata;
            try
            {
                switch (metadata.Language.Id)
                {
                    case "dart":
                        return LaunchDart(metadata, noDebug);
                    case "java":
                        return LaunchJava(metadata, noDebug);
                }
                var program = metadata.DebugProgram;
                if (program == null)
                {
                    // No debugger exists for this language here yet (WebAssembly): run the Run command instead.
                    if (!noDebug) Log("No debugger is configured for " + metadata.Language.DisplayName + " projects; running without debugging.");
                    package.RunProjectCommand(metadata, "run");
                    return VSConstants.S_OK;
                }
                if (!File.Exists(program))
                    throw new FileNotFoundException("The debug program " + program + " does not exist. Build the project first, or set Debug program in the project properties.");
                LaunchNative(metadata, program, noDebug);
                return VSConstants.S_OK;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidOperationException || e is COMException)
            {
                Log(e.Message);
                VsShellUtilities.ShowMessageBox(package, e.Message, metadata.Language.DisplayName + " debugging", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return VSConstants.S_OK;
            }
        }

        /// <summary>
        /// Rust (MSVC targets), Zig and MSVC-built C/C++ write CodeView PDBs next to the program, which the native engine
        /// reads; MinGW/DWARF builds bind no breakpoints here (lldb-dap remains the roadmap path for those).
        /// </summary>
        private void LaunchNative(LanguageProjectFile metadata, string program, bool noDebug)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = package.GetService<SVsShellDebugger, IVsDebugger4>();
            var target = new VsDebugTargetInfo4
            {
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = program,
                bstrArg = Core.Zig.ZigArguments.Join(metadata.DebugArguments),
                bstrCurDir = metadata.WorkingDirectory,
                guidLaunchDebugEngine = VSConstants.DebugEnginesGuids.NativeOnly_guid,
                LaunchFlags = noDebug ? (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_NoDebug : 0,
            };
            var results = new VsDebugTargetProcessInfo[1];
            Log((noDebug ? "Run: " : "Debug (native): ") + program + (target.bstrArg.Length == 0 ? "" : " " + target.bstrArg));
            debugger.LaunchDebugTargets4(1, new[] { target }, results);
        }

        private int LaunchDart(LanguageProjectFile metadata, bool noDebug)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var coordinator = package.GetMefService<Dart.DartDebugCoordinator>();
            var entry = Core.Dart.DartProject.Find(metadata.DirectoryPath)?.DefaultEntryPoint();
            package.JoinableTaskFactory.RunAsync(() => coordinator.DebugProgramAsync(entry, noDebug)).FileAndForget("Vistudio/Languages/DartLaunch");
            return VSConstants.S_OK;
        }

        /// <summary>Java runs in the Kotlin/JVM debug session (the Kotlin server's debugger serves Java frames, T30).</summary>
        private int LaunchJava(LanguageProjectFile metadata, bool noDebug)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var entry = FindJavaEntry(metadata.DirectoryPath)
                ?? throw new FileNotFoundException("No Java file with a main method was found under src\\main\\java.");
            var coordinator = package.GetMefService<KotlinDebugCoordinator>();
            var config = new JObject { ["type"] = KotlinLaunchProvider.LaunchType, ["project"] = entry, ["cwd"] = metadata.WorkingDirectory,
                ["args"] = new JArray(metadata.DebugArguments.ToArray()) };
            if (noDebug) config["noDebug"] = true;
            package.JoinableTaskFactory.RunAsync(() => coordinator.LaunchAsync(metadata.DirectoryPath, config, entry, CancellationToken.None, buildFirst: false))
                .FileAndForget("Vistudio/Languages/JavaLaunch");
            return VSConstants.S_OK;
        }

        private static string? FindJavaEntry(string folder)
        {
            var sources = Path.Combine(folder, "src", "main", "java");
            if (!Directory.Exists(sources)) return null;
            return Directory.EnumerateFiles(sources, "*.java", SearchOption.AllDirectories)
                .OrderBy(f => Path.GetFileName(f).Equals("Main.java", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => File.ReadAllText(f).Contains("static void main("));
        }
    }
}
