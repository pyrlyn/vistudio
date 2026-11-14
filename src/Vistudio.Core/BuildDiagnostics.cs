using System;
using System.Collections.Generic;
using System.Linq;

namespace Vistudio.Core
{
    // A new instance for each build. Both redirected streams may call Accept concurrently.
    public sealed class BuildDiagnostics
    {
        private readonly string root;
        private readonly object gate = new object();
        private readonly List<BuildDiagnostic> items = new List<BuildDiagnostic>();
        private readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

        public BuildDiagnostics(string root) => this.root = root;

        public void Accept(string line)
        {
            if (!GradleDiagnosticParser.TryParse(line, root, out var item) || item == null) return;
            var key = item.File.ToUpperInvariant() + "\n" + item.Line + ":" + item.Column + ":" + item.Severity + "\n" + item.Message;
            lock (gate) if (keys.Add(key)) items.Add(item);
        }

        public BuildDiagnostic[] Complete(int exitCode)
        {
            lock (gate)
            {
                var result = items.ToList();
                if (exitCode != 0 && !result.Any(x => x.Severity == BuildDiagnosticSeverity.Error))
                    result.Add(new BuildDiagnostic("", -1, -1,
                        "Gradle failed (exit " + exitCode + "). See Kotlin Output for details; no Kotlin source location was reported.", BuildDiagnosticSeverity.Error));
                return result.ToArray();
            }
        }
    }
}
