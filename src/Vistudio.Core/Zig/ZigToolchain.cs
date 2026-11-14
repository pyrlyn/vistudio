using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Zig
{
    /// <summary>What <c>zig env</c> reports: ZON since Zig 0.15, JSON before.</summary>
    public sealed class ZigEnvironmentInfo
    {
        public ZigEnvironmentInfo(string version, string libDirectory, string stdDirectory, string executable)
        { Version = version; LibDirectory = libDirectory; StdDirectory = stdDirectory; Executable = executable; }
        public string Version { get; }
        public string LibDirectory { get; }
        public string StdDirectory { get; }
        public string Executable { get; }

        private static readonly Regex Field = new Regex(@"^\s*\.?""?(?<key>zig_exe|lib_dir|std_dir|version)""?\s*[=:]\s*""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>Parses the top-level string fields of <c>zig env</c> output; null when no version is present.</summary>
        /// <param name="workingDirectory">Working directory of the <c>zig env</c> process; relative paths are resolved against it.</param>
        public static ZigEnvironmentInfo? Parse(string output, string? workingDirectory = null)
        {
            var info = Parse(output);
            if (info == null || string.IsNullOrEmpty(workingDirectory)) return info;
            string Absolute(string path) => path.Length == 0 || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workingDirectory, path));
            return new ZigEnvironmentInfo(info.Version, Absolute(info.LibDirectory), Absolute(info.StdDirectory), Absolute(info.Executable));
        }

        private static ZigEnvironmentInfo? Parse(string output)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Field.Matches(output))
                if (!values.ContainsKey(match.Groups["key"].Value)) values[match.Groups["key"].Value] = Unescape(match.Groups["value"].Value);
            if (!values.TryGetValue("version", out var version) || version.Length == 0) return null;
            values.TryGetValue("lib_dir", out var lib);
            values.TryGetValue("std_dir", out var std);
            values.TryGetValue("zig_exe", out var exe);
            if (string.IsNullOrEmpty(std) && !string.IsNullOrEmpty(lib)) std = Path.Combine(lib, "std");
            return new ZigEnvironmentInfo(version, lib ?? "", std ?? "", exe ?? "");
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { builder.Append(c); continue; }
                var next = value[++i];
                builder.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
            }
            return builder.ToString();
        }
    }

    public static class ZigToolchain
    {
        /// <summary>The configured zig.exe, else the first zig.exe on PATH; null when neither exists.</summary>
        public static string? FindExecutable(ZigSettings settings) => FindExecutable(settings.ZigPath, Environment.GetEnvironmentVariable("PATH"));

        public static string? FindExecutable(string? configured, string? path)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var value = Environment.ExpandEnvironmentVariables(configured!.Trim().Trim('"'));
                if (Directory.Exists(value)) value = Path.Combine(value, "zig.exe");
                return Path.IsPathRooted(value) && File.Exists(value) ? Path.GetFullPath(value) : null;
            }
            foreach (var folder in (path ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    var candidate = Path.Combine(folder.Trim().Trim('"'), "zig.exe");
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>zig.exe or a message for the user explaining how to configure it.</summary>
        public static string RequireExecutable(ZigSettings settings) =>
            FindExecutable(settings) ?? throw new FileNotFoundException(string.IsNullOrWhiteSpace(settings.ZigPath)
                ? "zig.exe was not found on PATH. Set Tools > Options > Zig > General > zig.exe path."
                : "The configured zig.exe does not exist: " + settings.ZigPath);

        /// <summary>The configured std library folder when it exists; validates that it contains <c>std/std.zig</c>.</summary>
        public static string? LibDirectory(ZigSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.LibDirectory)) return null;
            var folder = Environment.ExpandEnvironmentVariables(settings.LibDirectory.Trim().Trim('"'));
            if (!Path.IsPathRooted(folder) || !File.Exists(Path.Combine(folder, "std", "std.zig")))
                throw new DirectoryNotFoundException("The Zig library folder must contain std\\std.zig: " + settings.LibDirectory);
            return Path.GetFullPath(folder);
        }

        public static async Task<ZigEnvironmentInfo> ReadEnvironmentAsync(string zig, CancellationToken token)
        {
            var output = new StringBuilder();
            var start = ZigCommandFactory.Env(zig);
            var code = await OwnedProcess.RunAsync(start, line => { lock (output) output.AppendLine(line); }, token).ConfigureAwait(false);
            var info = code == 0 ? ZigEnvironmentInfo.Parse(output.ToString(), start.WorkingDirectory) : null;
            return info ?? throw new InvalidOperationException("'zig env' failed (exit code " + code + "): " + output.ToString().Trim());
        }
    }
}
