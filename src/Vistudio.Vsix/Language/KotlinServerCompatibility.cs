using System;
using System.Diagnostics;
using System.IO;

namespace Vistudio
{
    internal static class KotlinServerCompatibility
    {
        private const string HoverTimeoutOption = "-Ddap.evaluate.hover.timeout.ms=";

        internal static void Configure(ProcessStartInfo start)
        {
            // The official native server defaults hover evaluation to 500 ms, even
            // when VS grants 5000 ms to saved Watch expressions. Allow cold Kotlin
            // compilation, with headroom for presentation/transport before VS expires.
            // Keep the same hover request and restrictions; never retry user code.
            if (!string.Equals(Path.GetFileName(start.FileName), "intellij-server.exe", StringComparison.OrdinalIgnoreCase)) return;
            var options = start.EnvironmentVariables["IJ_JAVA_OPTIONS"] ?? "";
            // Preserve explicit user tuning and all unrelated JVM options verbatim.
            if (options.IndexOf(HoverTimeoutOption, StringComparison.Ordinal) >= 0) return;
            start.EnvironmentVariables["IJ_JAVA_OPTIONS"] = options +
                (options.Length == 0 ? "" : " ") + HoverTimeoutOption + "4000";
        }
    }
}
