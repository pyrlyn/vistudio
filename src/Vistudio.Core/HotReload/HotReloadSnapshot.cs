using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Vistudio.Core.HotReload
{
    /// <summary>One compiled class file as HotReload.gradle listed it.</summary>
    public sealed class HotReloadClassFile
    {
        public HotReloadClassFile(string path, string className, long length, long modified, string? hash = null)
        { Path = path; ClassName = className; Length = length; Modified = modified; Hash = hash; }
        public string Path { get; }
        /// <summary>Binary class name (<c>sample.Greeting</c>, <c>sample.Outer$Inner</c>), from the path below its class directory.</summary>
        public string ClassName { get; }
        public long Length { get; }
        /// <summary>Last write time in milliseconds since 1970-01-01 UTC, as the JVM reports it.</summary>
        public long Modified { get; }
        /// <summary>SHA-256 of the file (lower-case hex) when the list carries it: the list before a session's first compilation.</summary>
        public string? Hash { get; }
    }

    /// <summary>
    /// The class files of every JVM class output of a Gradle build at one moment (before.tsv, after.tsv), plus, in the
    /// list written after compilation, the reload agent jar and what was compiled.
    /// </summary>
    public sealed class HotReloadSnapshot
    {
        private HotReloadSnapshot(IReadOnlyList<HotReloadClassFile> classes, string? agentJar, string? module, string? tasks)
        { Classes = classes; AgentJar = agentJar; Module = module; Tasks = tasks; }

        public IReadOnlyList<HotReloadClassFile> Classes { get; }
        public string? AgentJar { get; }
        /// <summary>Gradle path of the module that owns the program's entry file.</summary>
        public string? Module { get; }
        public string? Tasks { get; }

        public static HotReloadSnapshot Empty { get; } = new HotReloadSnapshot(Array.Empty<HotReloadClassFile>(), null, null, null);

        public static HotReloadSnapshot Parse(string text)
        {
            var classes = new List<HotReloadClassFile>();
            string? directory = null, agentJar = null, module = null, tasks = null;
            foreach (var raw in text.Split('\n'))
            {
                var fields = raw.TrimEnd('\r').Split('\t');
                if (fields[0] == "D" && fields.Length == 2) directory = fields[1];
                else if (fields[0] == "A" && fields.Length == 2) agentJar = fields[1];
                else if (fields[0] == "M" && fields.Length == 3) { module = fields[1]; tasks = fields[2]; }
                else if (fields[0] == "C" && (fields.Length == 4 || fields.Length == 5) && directory != null)
                {
                    var name = ClassName(fields[1]);
                    if (name == null) continue;
                    if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length)) continue;
                    if (!long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var modified)) continue;
                    classes.Add(new HotReloadClassFile(System.IO.Path.Combine(directory, fields[1]), name, length, modified,
                        fields.Length == 5 && fields[4].Length != 0 ? fields[4] : null));
                }
            }
            return new HotReloadSnapshot(classes, agentJar, module, tasks);
        }

        /// <summary>
        /// The binary class name for a path below a class directory; null for files that are not reloadable classes
        /// (module descriptors, multi-release copies under META-INF).
        /// </summary>
        public static string? ClassName(string relativePath)
        {
            const string extension = ".class";
            if (!relativePath.EndsWith(extension, StringComparison.Ordinal)) return null;
            var name = relativePath.Substring(0, relativePath.Length - extension.Length).Replace('\\', '.').Replace('/', '.');
            if (name.Length == 0 || name == "module-info" || name.EndsWith(".module-info", StringComparison.Ordinal)) return null;
            if (name.StartsWith("META-INF.", StringComparison.Ordinal)) return null;
            return name;
        }
    }
}
