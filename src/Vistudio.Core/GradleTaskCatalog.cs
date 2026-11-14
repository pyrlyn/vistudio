using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vistudio.Core
{
    public sealed class GradleTaskInfo
    {
        public GradleTaskInfo(string path, string group, string description)
        { Path = path; Group = group; Description = description; }
        public string Path { get; }
        public string Group { get; }
        public string Description { get; }
        public override string ToString() => Path + "   [" + Group + "]   " + Description;
    }

    // The bundled init script emits only task metadata, without executing target tasks.
    public sealed class GradleTaskCatalog
    {
        private readonly Dictionary<string, GradleTaskInfo> tasks = new Dictionary<string, GradleTaskInfo>(StringComparer.Ordinal);
        public void Accept(string line)
        {
            var fields = line.Split('\t');
            if (fields.Length != 4 || fields[0] != "VISTUDIO_TASK") return;
            try
            {
                var path = GradleCommand.ParseTasks(fields[1]);
                if (path.Length != 1 || !path[0].StartsWith(":", StringComparison.Ordinal)) return;
                var task = new GradleTaskInfo(path[0], Decode(fields[2]), Decode(fields[3]));
                lock (tasks) tasks[task.Path] = task;
            }
            catch (ArgumentException) { }
            catch (FormatException) { }
        }
        public GradleTaskInfo[] Complete(int exitCode)
        {
            if (exitCode != 0) throw new InvalidOperationException("Gradle task discovery failed (exit " + exitCode + "). See Kotlin Output.");
            lock (tasks) return tasks.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        }
        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value)).Replace('\r', ' ').Replace('\n', ' ');
    }
}
