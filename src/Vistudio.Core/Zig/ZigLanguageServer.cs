using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Zig
{
    /// <summary>Launch and configuration of a user-installed ZLS. ZLS itself resolves @import and build.zig modules.</summary>
    public static class ZigLanguageServer
    {
        public static string ConfigFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "zls");

        /// <summary>
        /// zls.json folder of one workspace. Windows with different workspaces may use different zig.exe and std folders
        /// (<c>.vs\settings.json</c>), so they must not share one zls.json.
        /// </summary>
        public static string ConfigFolderFor(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return Path.Combine(ConfigFolder, "default");
            var key = Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/').ToUpperInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(ConfigFolder, string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))));
            }
        }

        /// <summary>
        /// ZLS configuration (zls.json keys): the toolchain from Tools &gt; Options &gt; Zig, no build-on-save (the full
        /// <c>zig build</c> is too heavy to run per save), and plain completion text because Visual Studio does not
        /// expand LSP snippets.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, object>> Configuration(string? zig, string? libDirectory) =>
            new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("zig_exe_path", zig ?? ""),
                new KeyValuePair<string, object>("zig_lib_path", libDirectory ?? ""),
                new KeyValuePair<string, object>("enable_build_on_save", false),
                new KeyValuePair<string, object>("enable_snippets", false),
                new KeyValuePair<string, object>("enable_argument_placeholders", false),
                new KeyValuePair<string, object>("semantic_tokens", "none"),
            }.Where(p => !(p.Value is string s) || s.Length > 0).ToList();

        public static string ConfigurationJson(string? zig, string? libDirectory)
        {
            var builder = new StringBuilder("{\n");
            var entries = Configuration(zig, libDirectory);
            for (var i = 0; i < entries.Count; i++)
            {
                builder.Append("  ").Append(Quote(entries[i].Key)).Append(": ");
                builder.Append(entries[i].Value switch
                {
                    bool b => b ? "true" : "false",
                    int n => n.ToString(CultureInfo.InvariantCulture),
                    _ => Quote(entries[i].Value.ToString()!),
                });
                builder.Append(i + 1 < entries.Count ? ",\n" : "\n");
            }
            return builder.Append("}\n").ToString();
        }

        /// <summary>Writes zls.json and returns ZLS's start information with <c>--config-path</c> and the user's arguments.</summary>
        public static ProcessStartInfo Create(ZigSettings settings, string? zig, string? libDirectory, string workingDirectory, string configFolder)
        {
            var server = Environment.ExpandEnvironmentVariables(settings.LanguageServerPath.Trim().Trim('"'));
            if (server.Length == 0 || !Path.IsPathRooted(server) || !File.Exists(server))
                throw new FileNotFoundException("Set Tools > Options > Zig > ZLS path to an existing zls.exe.", server);
            Directory.CreateDirectory(configFolder);
            var config = Path.Combine(configFolder, "zls.json");
            File.WriteAllText(config, ConfigurationJson(zig, libDirectory), new UTF8Encoding(false));
            var arguments = new List<string> { "--config-path", config };
            arguments.AddRange(ZigArguments.Split(settings.LanguageServerArguments));
            return ProcessCommands.LanguageServer(server, ZigArguments.Join(arguments), workingDirectory);
        }

        private static string Quote(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }
    }
}
