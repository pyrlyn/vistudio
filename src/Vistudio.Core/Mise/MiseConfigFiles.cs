using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Mise
{
    /// <summary>
    /// mise config file names (https://mise.jdx.dev/configuration.html): <c>mise.toml</c>, <c>.mise.toml</c>,
    /// <c>mise.local.toml</c>, <c>mise.&lt;env&gt;.toml</c>, <c>.config/mise.toml</c>, <c>mise/config.toml</c>,
    /// <c>.config/mise/config.toml</c>, <c>.config/mise/conf.d/*.toml</c> and the asdf-style <c>.tool-versions</c>.
    /// </summary>
    public static class MiseConfigFiles
    {
        private static readonly Regex MiseToml = new Regex(@"^\.?mise(\.[A-Za-z0-9_-]+)?(\.local)?\.toml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ConfigToml = new Regex(@"^config(\.[A-Za-z0-9_-]+)?(\.local)?\.toml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Sub-folders of a project folder that may hold its config; files there belong to the folder above.
        private static readonly string[] ConfigFolders = { Path.Combine(".config", "mise"), ".config", "mise", ".mise" };

        public static bool IsToolVersions(string path) => string.Equals(Path.GetFileName(path), ".tool-versions", StringComparison.OrdinalIgnoreCase);

        public static bool IsConfigFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string name, parent, grandParent;
            try
            {
                name = Path.GetFileName(path);
                var directory = Path.GetDirectoryName(path) ?? "";
                parent = Path.GetFileName(directory);
                grandParent = Path.GetFileName(Path.GetDirectoryName(directory) ?? "");
            }
            catch (ArgumentException) { return false; }
            if (IsToolVersions(path!)) return true;
            if (MiseToml.IsMatch(name)) return true;
            var miseFolder = parent.Equals("mise", StringComparison.OrdinalIgnoreCase) || parent.Equals(".mise", StringComparison.OrdinalIgnoreCase);
            if (miseFolder && ConfigToml.IsMatch(name)) return true;
            // .config/mise/conf.d/*.toml and mise/conf.d/*.toml
            return parent.Equals("conf.d", StringComparison.OrdinalIgnoreCase)
                && (grandParent.Equals("mise", StringComparison.OrdinalIgnoreCase) || grandParent.Equals(".mise", StringComparison.OrdinalIgnoreCase))
                && name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The folder whose tools a config file sets (the folder above <c>.config</c>, <c>mise</c>, <c>conf.d</c>).</summary>
        public static string ProjectFolder(string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
            if (Path.GetFileName(directory).Equals("conf.d", StringComparison.OrdinalIgnoreCase)) directory = Path.GetDirectoryName(directory) ?? directory;
            foreach (var folder in ConfigFolders)
            {
                var suffix = Path.DirectorySeparatorChar + folder;
                if (directory.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return directory.Substring(0, directory.Length - suffix.Length);
            }
            return directory;
        }

        /// <summary>Existing mise config files of one project folder (not its parents and not the global config).</summary>
        public static IReadOnlyList<string> Find(string? root)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;
            void AddFrom(string directory, Func<string, bool> accept)
            {
                if (!Directory.Exists(directory)) return;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                        if (accept(file)) result.Add(Path.GetFullPath(file));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            AddFrom(root!, file => IsToolVersions(file) || MiseToml.IsMatch(Path.GetFileName(file)));
            AddFrom(Path.Combine(root!, ".config"), file => MiseToml.IsMatch(Path.GetFileName(file)));
            foreach (var folder in new[] { Path.Combine(".config", "mise"), "mise", ".mise" })
            {
                AddFrom(Path.Combine(root!, folder), file => ConfigToml.IsMatch(Path.GetFileName(file)));
                AddFrom(Path.Combine(root!, folder, "conf.d"), file => file.EndsWith(".toml", StringComparison.OrdinalIgnoreCase));
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Normalized full path for comparisons (mise prints mixed separators, for example <c>C:\Users\me\.config/mise/config.toml</c>).</summary>
        public static string Normalize(string path)
        {
            try { return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar); }
            catch (ArgumentException) { return path; }
            catch (NotSupportedException) { return path; }
        }

        public static bool SamePath(string? left, string? right) =>
            left != null && right != null && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
