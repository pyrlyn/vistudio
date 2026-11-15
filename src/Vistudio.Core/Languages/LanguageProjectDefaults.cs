using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// The commands a new language project starts with: each language's own build tool, as its documentation runs it.
    /// Users change them in the Properties window; nothing here is applied to an existing project.
    /// </summary>
    public static class LanguageProjectDefaults
    {
        /// <param name="name">The built program's name, without .exe.</param>
        /// <param name="system">The build system; null picks Bazel when <paramref name="folder"/> is a Bazel workspace root.</param>
        public static IReadOnlyDictionary<string, string> For(LanguageInfo language, string folder, string name, LanguageBuildSystem? system = null)
        {
            var exe = name + ".exe";
            if ((system ?? Detect(folder)) == LanguageBuildSystem.Bazel)
            {
                // Bazel names programs after the target; the project's name is the root package's target by default.
                // Native programs (PDBs on Windows) debug in the native engine from bazel-bin.
                var native = language.Id == "rust" || language.Id == "zig" || language.Id == "c" || language.Id == "cpp";
                var bazel = Settings("bazel build //...", "bazel run //:" + name, "bazel test //...", "bazel clean", native ? @"bazel-bin\" + exe : "");
                return With(bazel, LanguageBuildSystem.Bazel);
            }
            return With(Native(language, folder, exe), LanguageBuildSystem.Native);
        }

        /// <summary>Bazel when the folder holds MODULE.bazel, REPO.bazel or WORKSPACE(.bazel); otherwise the language's own tool.</summary>
        public static LanguageBuildSystem Detect(string folder) =>
            BazelWorkspaceRoot(folder) ? LanguageBuildSystem.Bazel : LanguageBuildSystem.Native;

        private static bool BazelWorkspaceRoot(string folder)
        {
            try { return Directory.Exists(folder) && Bazel.BazelWorkspace.IsWorkspaceRoot(folder); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return false; }
        }

        private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> settings, LanguageBuildSystem system)
        {
            var result = settings.ToDictionary(p => p.Key, p => p.Value);
            result["BuildSystem"] = system.ToString();
            result["BuildCache"] = "true";
            result["RemoteCache"] = "";
            return result;
        }

        private static IReadOnlyDictionary<string, string> Native(LanguageInfo language, string folder, string exe)
        {
            switch (language.Id)
            {
                case "rust":
                    return Settings("cargo build", "cargo run", "cargo test", "cargo clean", @"target\debug\" + exe);
                case "zig":
                    return Settings("zig build", "zig build run", "zig build test", "", @"zig-out\bin\" + exe);
                case "dart":
                    return IsFlutter(folder)
                        ? Settings("flutter build windows --debug", "flutter run -d windows", "flutter test", "flutter clean", "")
                        : Settings("dart analyze", "dart run", "dart test", "", "");
                case "c":
                case "cpp":
                    // Conan's CMakeToolchain generator writes conan_toolchain.cmake into the output folder; the Debug
                    // build type keeps the MSVC runtime of the packages and of the program the same.
                    var configure = "cmake -S . -B build";
                    var conan = File.Exists(Path.Combine(folder, "conanfile.txt")) || File.Exists(Path.Combine(folder, "conanfile.py"));
                    var build = conan
                        ? "conan install . --output-folder=build --build=missing -s build_type=Debug && " + configure + " -DCMAKE_TOOLCHAIN_FILE=build/conan_toolchain.cmake && cmake --build build --config Debug"
                        : configure + " && cmake --build build --config Debug";
                    return Settings(build, @"build\Debug\" + exe, "ctest --test-dir build -C Debug", "cmake --build build --target clean --config Debug", @"build\Debug\" + exe);
                case "wasm":
                    return Settings(@"wasm-tools parse src\main.wat -o build\main.wasm", @"wasmtime run build\main.wasm", "", "", "");
                case "java":
                    return Settings("gradlew.bat build", "gradlew.bat run", "gradlew.bat test", "gradlew.bat clean", "");
                default:
                    throw new ArgumentException("No language project defaults for " + language.DisplayName + ".");
            }
        }

        /// <summary>Where bazelisk/bazel usually land when installed with Go or by hand.</summary>
        public static IEnumerable<string> BazelInstallFolders()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[] { Path.Combine(home, "go", "bin"), Path.Combine(home, "bin") };
        }

        /// <summary>Where each language's installer puts its tools when PATH was not updated for Visual Studio yet.</summary>
        public static IEnumerable<string> InstallFolders(LanguageInfo language)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (language.Id)
            {
                case "rust": return new[] { Path.Combine(home, ".cargo", "bin") };
                case "wasm": return new[] { Path.Combine(home, ".cargo", "bin"), Path.Combine(home, ".wasmtime", "bin") };
                case "c":
                case "cpp":
                    // The CMake bundled with the running Visual Studio knows that Visual Studio's generator; an older
                    // CMake on PATH (3.31 has no "Visual Studio 18 2026") fails to configure. devenv sets VSAPPIDDIR
                    // (its Common7\IDE folder) for the processes it starts.
                    var ide = Environment.GetEnvironmentVariable("VSAPPIDDIR");
                    return string.IsNullOrEmpty(ide) ? Enumerable.Empty<string>()
                        : new[] { Path.Combine(ide, "CommonExtensions", "Microsoft", "CMake", "CMake", "bin") };
                default: return Enumerable.Empty<string>();
            }
        }

        /// <summary>A pubspec that depends on the Flutter SDK (<c>flutter: sdk: flutter</c>).</summary>
        public static bool IsFlutter(string folder)
        {
            var pubspec = Path.Combine(folder, "pubspec.yaml");
            if (!File.Exists(pubspec)) return false;
            try { return Dart.DartProject.FromPubspec(folder, File.ReadAllText(pubspec)).IsFlutter; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException) { return false; }
        }

        private static IReadOnlyDictionary<string, string> Settings(string build, string run, string test, string clean, string debugProgram) =>
            new Dictionary<string, string>
            {
                ["BuildCommand"] = build, ["RunCommand"] = run, ["TestCommand"] = test, ["CleanCommand"] = clean,
                ["DebugProgram"] = debugProgram, ["WorkingDirectory"] = ".",
            };
    }
}
