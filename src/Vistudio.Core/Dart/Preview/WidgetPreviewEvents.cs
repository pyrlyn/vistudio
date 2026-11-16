using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart.Preview
{
    public enum PreviewerEventKind { Initializing, Started, Log, Other }

    /// <summary>What a previewer log line means for the window.</summary>
    public enum PreviewerStatus
    {
        None,
        /// <summary>A watched file changed; a hot reload follows.</summary>
        ReloadStarted,
        /// <summary>A hot restart was requested (by the scaffold, a pubspec change or Vistudio).</summary>
        RestartStarted,
        Reloaded,
        Restarted,
        /// <summary>The hot reload was rejected or failed; a hot restart is needed.</summary>
        ReloadFailed,
        /// <summary>The hot restart did not reach every page (for example a page that went away without disconnecting); the pages must be reloaded.</summary>
        RestartFailed,
        /// <summary>The recompile finished but the page must be reloaded (older module formats).</summary>
        PageRefreshRequired,
        /// <summary>The scaffold is running and the previews are loaded.</summary>
        Ready,
        /// <summary>A compiler or build error.</summary>
        Error,
        /// <summary>The previewer lost its DTD connection; updates stop.</summary>
        ConnectionLost,
    }

    /// <summary>One <c>flutter widget-preview start --machine</c> event (<c>[{"event":"widget_preview.started","params":{...}}]</c>).</summary>
    public sealed class PreviewerEvent
    {
        public PreviewerEvent(PreviewerEventKind kind, string name, string? url, int? pid, string? level, string? message)
        { Kind = kind; Name = name; Url = url; Pid = pid; Level = level; Message = message; }
        public PreviewerEventKind Kind { get; }
        /// <summary>The event name without the <c>widget_preview.</c> prefix.</summary>
        public string Name { get; }
        public string? Url { get; }
        public int? Pid { get; }
        /// <summary><c>status</c>, <c>warning</c>, <c>error</c> or <c>trace</c> for log messages.</summary>
        public string? Level { get; }
        public string? Message { get; }

        /// <summary>Parses one stdout line; null for anything that is not a previewer event.</summary>
        public static PreviewerEvent? Parse(string line)
        {
            var text = line.Trim();
            if (text.Length < 4 || text[0] != '[' || text[text.Length - 1] != ']') return null;
            JArray array;
            try { array = JArray.Parse(text); }
            catch (JsonException) { return null; }
            if (array.Count != 1 || !(array[0] is JObject message)) return null;
            var name = (string?)message["event"];
            if (name == null || !name.StartsWith("widget_preview.", StringComparison.Ordinal)) return null;
            name = name.Substring("widget_preview.".Length);
            var parameters = message["params"] as JObject ?? new JObject();
            switch (name)
            {
                case "initializing": return new PreviewerEvent(PreviewerEventKind.Initializing, name, null, parameters["pid"]?.Type == JTokenType.Integer ? (int?)parameters["pid"] : null, null, null);
                case "started": return new PreviewerEvent(PreviewerEventKind.Started, name, (string?)parameters["url"], null, null, null);
                case "logMessage":
                    var stack = (string?)parameters["stackTrace"];
                    var text2 = (string?)parameters["message"] ?? "";
                    return new PreviewerEvent(PreviewerEventKind.Log, name, null, null, (string?)parameters["level"] ?? "status", stack == null ? text2 : text2 + Environment.NewLine + stack);
                default: return new PreviewerEvent(PreviewerEventKind.Other, name, null, null, null, parameters.ToString(Formatting.None));
            }
        }

        /// <summary>Classifies a log message by the texts the Flutter tool prints (flutter_tools widget_preview and resident_web_runner).</summary>
        public PreviewerStatus Status
        {
            get
            {
                if (Kind != PreviewerEventKind.Log || Message == null) return PreviewerStatus.None;
                var m = Message;
                if (m.StartsWith("Triggering reload", StringComparison.Ordinal)) return PreviewerStatus.ReloadStarted;
                if (m.StartsWith("Triggering restart", StringComparison.Ordinal)) return PreviewerStatus.RestartStarted;
                if (m.StartsWith("Reloaded application in", StringComparison.Ordinal)) return PreviewerStatus.Reloaded;
                if (m.StartsWith("Restarted application in", StringComparison.Ordinal)) return PreviewerStatus.Restarted;
                if (m.StartsWith("Recompile complete. Page requires refresh", StringComparison.Ordinal)) return PreviewerStatus.PageRefreshRequired;
                if (m.StartsWith("Done loading previews", StringComparison.Ordinal)) return PreviewerStatus.Ready;
                // DWDS waits for an answer from every page it counts; seen in Visual Studio: "Hot restart failed:
                // TimeoutException after 0:00:15.000000: Hot restart timed out - received 1/3 responses".
                if (m.IndexOf("Hot restart failed", StringComparison.OrdinalIgnoreCase) >= 0) return PreviewerStatus.RestartFailed;
                if (m.IndexOf("Hot reload failed", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Hot reload rejected", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("Error during widget preview reload", StringComparison.Ordinal) >= 0)
                    return PreviewerStatus.ReloadFailed;
                if (m.IndexOf("Lost connection to the Dart Tooling Daemon", StringComparison.Ordinal) >= 0) return PreviewerStatus.ConnectionLost;
                if (Level == "error") return PreviewerStatus.Error;
                return PreviewerStatus.None;
            }
        }
    }
}
