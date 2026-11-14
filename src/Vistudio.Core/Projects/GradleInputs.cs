using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.Projects
{
    /// <summary>
    /// IDE-side fast up-to-date check for a Gradle build tree. A snapshot of input file paths is taken when a build starts;
    /// the tree is up to date while the same files exist and none was written after that moment. Gradle itself remains the
    /// incremental build authority. Directory timestamps are not used because Gradle creates output folders in input folders.
    /// </summary>
    public static class GradleInputs
    {
        private static readonly string[] IgnoredDirectories = { "build", "out", ".gradle", ".kotlin", ".idea", ".vs", ".git" };
        private static readonly string[] IgnoredExtensions = { ".kotlinproj", ".sln", ".slnx", ".suo", ".user", ".tmp" };

        public sealed class Snapshot
        {
            internal Snapshot(DateTime takenUtc, string[] files) { TakenUtc = takenUtc; Files = files; }
            public DateTime TakenUtc { get; }
            public IReadOnlyList<string> Files { get; }
        }

        /// <summary>Records the input set; the time is taken before scanning so edits during the scan count as changes.</summary>
        public static Snapshot Capture(string root)
        {
            var taken = DateTime.UtcNow;
            return new Snapshot(taken, Inputs(root).Select(f => f.FullName).ToArray());
        }

        /// <summary>Up to date only after a successful build in this session with the same inputs, none written since it started.</summary>
        public static bool IsUpToDate(string root, Snapshot? lastSuccessfulBuild)
        {
            if (lastSuccessfulBuild == null) return false;
            var index = 0;
            foreach (var file in Inputs(root))
            {
                if (index >= lastSuccessfulBuild.Files.Count || !string.Equals(file.FullName, lastSuccessfulBuild.Files[index], StringComparison.OrdinalIgnoreCase)) return false;
                if (file.LastWriteTimeUtc >= lastSuccessfulBuild.TakenUtc) return false;
                index++;
            }
            return index == lastSuccessfulBuild.Files.Count;
        }

        private static IEnumerable<FileInfo> Inputs(string root)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                var entries = directory.GetFileSystemInfos();
                Array.Sort(entries, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                foreach (var file in entries.OfType<FileInfo>())
                    if (!IgnoredExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) yield return file;
                foreach (var child in entries.OfType<DirectoryInfo>().Reverse())
                    if (!IgnoredDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase) && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
            }
        }
    }
}
