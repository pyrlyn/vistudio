using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vistudio.Core.Bazel;
using Vistudio.Core.Packages;

namespace Vistudio.Core.Languages
{
    /// <summary>Inputs of <see cref="BuildCache.Apply"/>; the lookups are replaceable for tests.</summary>
    public sealed class BuildCacheOptions
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Remote cache URL (Bazel <c>--remote_cache</c>, Gradle HTTP build cache); empty for none.</summary>
        public string RemoteCache { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string BazelDiskCache { get; set; } = "";
        /// <summary>Folder for files Vistudio writes for the cache (the Gradle remote cache init script).</summary>
        public string StateFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio");
        public Func<string, string?> FindTool { get; set; } = name => ToolLocator.Find(new[] { name });
        public Func<string, string?> GetEnvironment { get; set; } = Environment.GetEnvironmentVariable;
    }

    /// <summary>The command after <see cref="BuildCache.Apply"/>: arguments plus environment variables to set.</summary>
    public sealed class BuildCacheResult
    {
        internal BuildCacheResult(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string? note)
        {
            Arguments = arguments; Environment = environment; Note = note;
        }

        public IReadOnlyList<string> Arguments { get; }
        public IReadOnlyDictionary<string, string> Environment { get; }
        /// <summary>What was turned on, or why nothing was (for the output pane); null when the tool has no cache to add.</summary>
        public string? Note { get; }
    }

    /// <summary>
    /// Turns on each build tool's own cache for a project command when the project's <c>BuildCache</c> setting is on
    /// (creator decision: tool-native caches, on by default). Nothing is added that the user already chose: an explicit
    /// flag on the command line, <c>org.gradle.caching</c> in gradle.properties, <c>disk_cache</c>/<c>remote_cache</c> in
    /// .bazelrc, or <c>RUSTC_WRAPPER</c> in the environment wins.
    /// <list type="bullet">
    /// <item>Bazel (bazel, bazelisk): <c>--disk_cache</c> for build, test, run and coverage; <c>--remote_cache</c> when set.</item>
    /// <item>Gradle (gradlew, gradle; Kotlin and Java): <c>--build-cache</c>; an HTTP remote cache (read-only) through an init script.</item>
    /// <item>Cargo (Rust): sccache as <c>RUSTC_WRAPPER</c> when sccache is installed.</item>
    /// <item>CMake configure (C, C++): sccache as the compiler launcher when installed; Visual Studio generators ignore
    /// launchers, so it takes effect with Ninja or Makefile generators.</item>
    /// <item>Zig, Dart/Flutter and wasm-tools: their caches are built in (Zig's .zig-cache and global cache, Dart's
    /// snapshots, Flutter's own Gradle/CMake builds); nothing is added.</item>
    /// </list>
    /// </summary>
    public static class BuildCache
    {
        public const string GradleRemoteVariable = "VISTUDIO_GRADLE_REMOTE_CACHE";
        private static readonly string[] BazelCachedCommands = { "build", "test", "run", "coverage" };
        private static readonly string[] CargoCachedCommands = { "build", "run", "test", "check", "bench", "doc", "clippy" };

        public static BuildCacheResult Apply(string program, IReadOnlyList<string> arguments, BuildCacheOptions options)
        {
            var none = new Dictionary<string, string>();
            if (!options.Enabled) return new BuildCacheResult(arguments, none, null);
            switch (Tool(program))
            {
                case "bazel": return Bazel(arguments, options);
                case "gradle": return Gradle(arguments, options);
                case "cargo": return Cargo(arguments, options);
                case "cmake": return CMake(arguments, options);
                default: return new BuildCacheResult(arguments, none, null);
            }
        }

        /// <summary>The tool family of a program path or name: bazel, gradle, cargo, cmake, or its own name.</summary>
        public static string Tool(string program)
        {
            var name = Path.GetFileNameWithoutExtension(program ?? "").ToLowerInvariant();
            switch (name)
            {
                case "bazel": case "bazelisk": return "bazel";
                case "gradlew": case "gradle": return "gradle";
                default: return name;
            }
        }

        /// <summary>What the cache setting does for a language's usual tool, for the Properties window.</summary>
        public static string Describe(LanguageInfo language, LanguageBuildSystem system)
        {
            if (system == LanguageBuildSystem.Bazel) return "Bazel: --disk_cache (Tools > Options > Bazel > Disk cache folder) and --remote_cache when Remote cache is set.";
            switch (language.Id)
            {
                case "java": case "kotlin": return "Gradle: --build-cache; an HTTP Remote cache is read through an init script.";
                case "rust": return "Cargo: sccache as RUSTC_WRAPPER when sccache is installed.";
                case "c": case "cpp": return "CMake: sccache as compiler launcher when installed (Ninja or Makefile generators); Conan keeps its own package cache.";
                case "zig": return "Zig caches every build itself (.zig-cache and the global cache); nothing to add.";
                case "dart": return "Dart and Flutter cache their own builds; nothing to add.";
                default: return "wasm-tools has no build cache; nothing to add.";
            }
        }

        private static BuildCacheResult Bazel(IReadOnlyList<string> arguments, BuildCacheOptions options)
        {
            var command = arguments.Select((a, i) => (a, i)).FirstOrDefault(p => !p.a.StartsWith("-", StringComparison.Ordinal));
            if (command.a == null || !BazelCachedCommands.Contains(command.a)) return new BuildCacheResult(arguments, new Dictionary<string, string>(), null);
            var rc = BazelRc(options.WorkingDirectory);
            var added = new List<string>();
            if (!Has(arguments, "--disk_cache") && !rc.Contains("disk_cache") && options.BazelDiskCache.Length != 0)
                added.Add("--disk_cache=" + options.BazelDiskCache);
            if (options.RemoteCache.Length != 0 && !Has(arguments, "--remote_cache") && !rc.Contains("remote_cache"))
                added.Add("--remote_cache=" + options.RemoteCache);
            var result = arguments.Take(command.i + 1).Concat(added).Concat(arguments.Skip(command.i + 1)).ToList();
            var note = added.Count == 0 ? "Bazel cache: already configured (command line or .bazelrc)." : "Bazel cache: " + string.Join(" ", added);
            return new BuildCacheResult(result, new Dictionary<string, string>(), note);
        }

        /// <summary>The workspace's .bazelrc files (root and user), for flags the user already set.</summary>
        private static string BazelRc(string directory)
        {
            var text = "";
            var root = BazelWorkspace.FindRoot(directory);
            foreach (var file in new[] { root == null ? null : Path.Combine(root, ".bazelrc"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bazelrc") })
            {
                try { if (file != null && File.Exists(file)) text += File.ReadAllText(file) + "\n"; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            // Comments do not count.
            return string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));
        }

        private static BuildCacheResult Gradle(IReadOnlyList<string> arguments, BuildCacheOptions options)
        {
            var environment = new Dictionary<string, string>();
            var result = arguments.ToList();
            var notes = new List<string>();
            if (Has(arguments, "--build-cache") || Has(arguments, "--no-build-cache")) notes.Add("build cache set on the command line");
            else if (GradleProperty(options.WorkingDirectory, "org.gradle.caching") != null) notes.Add("build cache set in gradle.properties");
            else { result.Insert(0, "--build-cache"); notes.Add("--build-cache"); }
            if (options.RemoteCache.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var script = GradleRemoteScript(options.StateFolder);
                result.Insert(0, script);
                result.Insert(0, "--init-script");
                environment[GradleRemoteVariable] = options.RemoteCache;
                notes.Add("remote cache " + options.RemoteCache + " (read-only)");
            }
            return new BuildCacheResult(result, environment, "Gradle cache: " + string.Join(", ", notes) + ".");
        }

        /// <summary>A gradle.properties value of the project folder or Gradle user home; null when not set.</summary>
        public static string? GradleProperty(string directory, string key)
        {
            var home = Environment.GetEnvironmentVariable("GRADLE_USER_HOME");
            if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");
            foreach (var file in new[] { Path.Combine(directory, "gradle.properties"), Path.Combine(home!, "gradle.properties") })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var match = Regex.Match(line, @"^\s*" + Regex.Escape(key) + @"\s*[=:]\s*(.*)$");
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>
        /// Writes (once) the init script that adds an HTTP remote build cache before the build's settings are evaluated, so
        /// a cache the project configures itself still wins. Pulls only: pushing is left to CI.
        /// </summary>
        public static string GradleRemoteScript(string folder)
        {
            const string content =
                "// Written by Vistudio: the remote build cache of a project's RemoteCache setting (read-only).\n" +
                "def vistudioCacheUrl = System.getenv('" + GradleRemoteVariable + "')\n" +
                "if (vistudioCacheUrl) {\n" +
                "    gradle.beforeSettings { settings ->\n" +
                "        settings.buildCache {\n" +
                "            remote(HttpBuildCache) {\n" +
                "                url = new URI(vistudioCacheUrl)\n" +
                "                push = false\n" +
                "                allowInsecureProtocol = vistudioCacheUrl.startsWith('http:')\n" +
                "            }\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var path = Path.Combine(folder, "gradle-remote-cache.gradle");
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, content);
            }
            return path;
        }

        private static BuildCacheResult Cargo(IReadOnlyList<string> arguments, BuildCacheOptions options)
        {
            var environment = new Dictionary<string, string>();
            var command = arguments.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal) && !a.StartsWith("+", StringComparison.Ordinal));
            if (command == null || !CargoCachedCommands.Contains(command)) return new BuildCacheResult(arguments, environment, null);
            if (!string.IsNullOrEmpty(options.GetEnvironment("RUSTC_WRAPPER")))
                return new BuildCacheResult(arguments, environment, "Rust cache: RUSTC_WRAPPER is already set.");
            var sccache = options.FindTool("sccache");
            if (sccache == null) return new BuildCacheResult(arguments, environment, "Rust cache: sccache not found; Cargo's incremental cache only.");
            environment["RUSTC_WRAPPER"] = sccache;
            return new BuildCacheResult(arguments, environment, "Rust cache: RUSTC_WRAPPER=" + sccache);
        }

        private static BuildCacheResult CMake(IReadOnlyList<string> arguments, BuildCacheOptions options)
        {
            var environment = new Dictionary<string, string>();
            var configure = !arguments.Contains("--build") && !arguments.Contains("--install") && !arguments.Contains("-E")
                && arguments.Any(a => a == "-S" || a == "-B" || a.StartsWith("-S", StringComparison.Ordinal) || a.StartsWith("-B", StringComparison.Ordinal));
            if (!configure) return new BuildCacheResult(arguments, environment, null);
            if (arguments.Any(a => a.Contains("COMPILER_LAUNCHER")))
                return new BuildCacheResult(arguments, environment, "C/C++ cache: compiler launcher set on the command line.");
            var sccache = options.FindTool("sccache");
            if (sccache == null) return new BuildCacheResult(arguments, environment, "C/C++ cache: sccache not found; no compiler cache.");
            var launcher = sccache.Replace('\\', '/');
            var result = arguments.Concat(new[]
            {
                "-DCMAKE_C_COMPILER_LAUNCHER=" + launcher, "-DCMAKE_CXX_COMPILER_LAUNCHER=" + launcher,
                // sccache caches MSVC objects only with debug information inside them (/Z7), CMake 3.25+.
                "-DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded",
            }).ToList();
            return new BuildCacheResult(result, environment, "C/C++ cache: sccache compiler launcher (Ninja/Makefile generators).");
        }

        private static bool Has(IEnumerable<string> arguments, string flag) =>
            arguments.Any(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal));
    }

    public enum LanguageBuildSystem { Native, Bazel }
}
