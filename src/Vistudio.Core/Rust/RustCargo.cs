using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Rust
{
    /// <summary>Cargo commands for a Rust source file that is not part of a <c>.rustproj</c>.</summary>
    public static class RustCargo
    {
        /// <summary>
        /// The package folder (nearest <c>Cargo.toml</c> above the file) and the <c>cargo run</c> arguments that run the
        /// program defined by <paramref name="sourceFile"/>: Cargo's auto-discovered targets <c>src/bin/&lt;name&gt;.rs</c>,
        /// <c>src/bin/&lt;name&gt;/main.rs</c> and <c>examples/&lt;name&gt;.rs</c> are named, anything else runs the default binary.
        /// Null when no Cargo.toml is found.
        /// </summary>
        public static (string Folder, IReadOnlyList<string> Arguments)? RunCommand(string sourceFile)
        {
            var full = Path.GetFullPath(sourceFile);
            for (var folder = Path.GetDirectoryName(full); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (!File.Exists(Path.Combine(folder, "Cargo.toml"))) continue;
                var relative = full.Substring(folder!.Length).TrimStart('\\', '/').Split('\\', '/');
                var arguments = new List<string> { "run" };
                if (relative.Length == 3 && Is(relative[0], "src") && Is(relative[1], "bin") && Is(Path.GetExtension(relative[2]), ".rs"))
                    arguments.AddRange(new[] { "--bin", Path.GetFileNameWithoutExtension(relative[2]) });
                else if (relative.Length == 4 && Is(relative[0], "src") && Is(relative[1], "bin") && Is(relative[3], "main.rs"))
                    arguments.AddRange(new[] { "--bin", relative[2] });
                else if (relative.Length == 2 && Is(relative[0], "examples") && Is(Path.GetExtension(relative[1]), ".rs"))
                    arguments.AddRange(new[] { "--example", Path.GetFileNameWithoutExtension(relative[1]) });
                return (folder, arguments);
            }
            return null;
        }

        /// <summary>
        /// The <c>cargo test</c> run of one test of <paramref name="sourceFile"/>. <paramref name="testPath"/> is the test's path
        /// inside the file (<c>tests::adds</c>); the file's place in the package adds the module path (<c>src/net/http.rs</c>:
        /// <c>net::http::</c>) and the target (<c>src/bin/x.rs</c>: <c>--bin x</c>, <c>tests/x.rs</c>: <c>--test x</c>,
        /// <c>examples</c>, <c>benches</c>), so the test binary's <c>--exact</c> filter names exactly that test.
        /// Null when no Cargo.toml is found above the file.
        /// </summary>
        public static RustTestCommand? TestCommand(string sourceFile, string testPath)
        {
            var full = Path.GetFullPath(sourceFile);
            for (var folder = Path.GetDirectoryName(full); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (!File.Exists(Path.Combine(folder, "Cargo.toml"))) continue;
                var parts = full.Substring(folder!.Length).TrimStart('\\', '/').Split('\\', '/');
                var target = new List<string>();
                var modules = new List<string>();
                string crateRoot;
                int moduleStart;
                if (parts.Length >= 3 && Is(parts[0], "src") && Is(parts[1], "bin"))
                {
                    // src/bin/x.rs, or src/bin/x/main.rs with its modules next to it.
                    var single = parts.Length == 3;
                    var name = single ? Path.GetFileNameWithoutExtension(parts[2]) : parts[2];
                    target.AddRange(new[] { "--bin", name });
                    crateRoot = single ? Path.Combine(folder, "src", "bin", parts[2]) : Path.Combine(folder, "src", "bin", name, "main.rs");
                    moduleStart = single ? parts.Length : 3;
                }
                else if (parts.Length >= 2 && (Is(parts[0], "tests") || Is(parts[0], "examples") || Is(parts[0], "benches")))
                {
                    var single = parts.Length == 2;
                    var name = single ? Path.GetFileNameWithoutExtension(parts[1]) : parts[1];
                    var flag = Is(parts[0], "tests") ? "--test" : Is(parts[0], "examples") ? "--example" : "--bench";
                    target.AddRange(new[] { flag, name });
                    crateRoot = single ? Path.Combine(folder, parts[0], parts[1]) : Path.Combine(folder, parts[0], name, "main.rs");
                    moduleStart = single ? parts.Length : 2;
                }
                else
                {
                    // The library when there is one, else the package's binary.
                    var library = Path.Combine(folder, "src", "lib.rs");
                    crateRoot = File.Exists(library) ? library : Path.Combine(folder, "src", "main.rs");
                    if (!File.Exists(library) && File.Exists(crateRoot)) target.AddRange(new[] { "--bin", PackageName(folder) ?? Path.GetFileName(folder) });
                    else if (File.Exists(library)) target.Add("--lib");
                    moduleStart = parts.Length > 0 && Is(parts[0], "src") ? 1 : parts.Length;
                }
                for (var i = moduleStart; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (i == parts.Length - 1)
                    {
                        var stem = Path.GetFileNameWithoutExtension(part);
                        // mod.rs, and the crate roots main.rs / lib.rs, add no module of their own.
                        if (!(Is(stem, "mod") || (i == moduleStart && (Is(stem, "main") || Is(stem, "lib"))))) modules.Add(stem);
                    }
                    else modules.Add(part);
                }
                var filter = string.Join("::", modules.Concat(new[] { testPath }));
                var run = new List<string> { "test" };
                run.AddRange(target);
                run.AddRange(new[] { "--", filter, "--exact" });
                var build = new List<string> { "test" };
                build.AddRange(target);
                build.AddRange(new[] { "--no-run", "--message-format=json" });
                return new RustTestCommand(folder, run, build, crateRoot, filter);
            }
            return null;
        }

        /// <summary>
        /// The test program cargo built for <paramref name="crateRoot"/>, from <c>cargo test --no-run --message-format=json</c>
        /// output (a <c>compiler-artifact</c> with <c>profile.test</c> and an <c>executable</c>). Null when there is none.
        /// </summary>
        public static string? TestExecutable(IEnumerable<string> jsonLines, string crateRoot)
        {
            string? any = null;
            var wanted = Path.GetFullPath(crateRoot);
            foreach (var line in jsonLines)
            {
                if (line == null || !line.StartsWith("{", StringComparison.Ordinal)) continue;
                JObject message;
                try { message = JObject.Parse(line); } catch (Newtonsoft.Json.JsonException) { continue; }
                if ((string?)message["reason"] != "compiler-artifact" || message["profile"]?["test"]?.Type != JTokenType.Boolean || !(bool)message["profile"]!["test"]!) continue;
                var executable = (string?)message["executable"];
                if (string.IsNullOrEmpty(executable)) continue;
                var source = (string?)message["target"]?["src_path"];
                if (source != null && string.Equals(Path.GetFullPath(source), wanted, StringComparison.OrdinalIgnoreCase)) return executable;
                any ??= executable;
            }
            return any;
        }

        /// <summary>The human-readable text of a <c>compiler-message</c> line of <c>--message-format=json</c> output, or null for any other line.</summary>
        public static string? RenderedMessage(string line)
        {
            if (line == null || !line.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                var message = JObject.Parse(line);
                return (string?)message["reason"] == "compiler-message" ? ((string?)message["message"]?["rendered"])?.TrimEnd() : null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        /// <summary>The <c>[package] name</c> of a Cargo.toml (binary targets default to it), or null.</summary>
        private static string? PackageName(string folder)
        {
            try
            {
                var inPackage = false;
                foreach (var raw in File.ReadLines(Path.Combine(folder, "Cargo.toml")))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal)) { inPackage = line == "[package]"; continue; }
                    if (!inPackage || !line.StartsWith("name", StringComparison.Ordinal)) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var value = line.Substring(eq + 1).Trim().Trim('"', '\'');
                    if (value.Length > 0) return value;
                }
            }
            catch (IOException) { }
            return null;
        }

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One test's <c>cargo test</c> commands (see <see cref="RustCargo.TestCommand"/>).</summary>
    public sealed class RustTestCommand
    {
        internal RustTestCommand(string folder, IReadOnlyList<string> runArguments, IReadOnlyList<string> buildArguments, string crateRoot, string filter)
        {
            Folder = folder; RunArguments = runArguments; BuildArguments = buildArguments; CrateRoot = crateRoot; Filter = filter;
        }

        /// <summary>The package folder (Cargo.toml); cargo runs tests there, and so does the debugger.</summary>
        public string Folder { get; }
        /// <summary><c>test [target] -- &lt;filter&gt; --exact</c>.</summary>
        public IReadOnlyList<string> RunArguments { get; }
        /// <summary><c>test [target] --no-run --message-format=json</c>: builds the test program for the debugger.</summary>
        public IReadOnlyList<string> BuildArguments { get; }
        /// <summary>The crate root the test belongs to, which picks the test program out of cargo's artifacts.</summary>
        public string CrateRoot { get; }
        /// <summary>The test's full path in its crate (<c>net::http::tests::adds</c>).</summary>
        public string Filter { get; }
        /// <summary>Arguments for the test program itself: only that test, output not captured.</summary>
        public IReadOnlyList<string> ProgramArguments => new[] { Filter, "--exact", "--nocapture" };
    }
}
