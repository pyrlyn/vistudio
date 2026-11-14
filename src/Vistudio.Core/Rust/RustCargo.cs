using System;
using System.Collections.Generic;
using System.IO;

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

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
