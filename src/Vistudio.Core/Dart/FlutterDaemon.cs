using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart
{
    /// <summary>One message of the Flutter daemon protocol (<c>flutter run --machine</c>): an event, or a response to a request.</summary>
    public sealed class FlutterDaemonMessage
    {
        private FlutterDaemonMessage(string? @event, JObject? parameters, int? id, JToken? result, JToken? error)
        { Event = @event; Params = parameters ?? new JObject(); Id = id; Result = result; Error = error; }

        /// <summary><c>app.start</c>, <c>app.started</c>, <c>app.log</c>, <c>app.progress</c>, <c>app.stop</c>, <c>daemon.logMessage</c>, ...; null for responses.</summary>
        public string? Event { get; }
        public JObject Params { get; }
        /// <summary>Request id this message answers; null for events.</summary>
        public int? Id { get; }
        public JToken? Result { get; }
        public JToken? Error { get; }
        public bool IsResponse => Id != null && Event == null;

        public string? AppId => (string?)Params["appId"];

        /// <summary>
        /// Parses one stdout line. Daemon messages are single-element JSON arrays (<c>[{"event":...}]</c>); any other line
        /// (build output, warnings) returns null and is shown as plain text.
        /// </summary>
        public static FlutterDaemonMessage? Parse(string line)
        {
            var text = line.Trim();
            if (text.Length < 4 || text[0] != '[' || text[text.Length - 1] != ']') return null;
            JArray array;
            try { array = JArray.Parse(text); }
            catch (JsonException) { return null; }
            if (array.Count != 1 || !(array[0] is JObject message)) return null;
            var id = message["id"]?.Type == JTokenType.Integer ? (int?)message["id"] : null;
            var @event = (string?)message["event"];
            if (@event == null && id == null) return null;
            return new FlutterDaemonMessage(@event, message["params"] as JObject, id, message["result"], message["error"]);
        }

        /// <summary>For responses to <c>app.restart</c>: <c>{"code":0,"message":""}</c> is success; anything else fails with its message.</summary>
        public bool Succeeded(out string message)
        {
            if (Error != null && Error.Type != JTokenType.Null) { message = Error.Type == JTokenType.String ? (string)Error! : Error.ToString(Formatting.None); return false; }
            if (Result is JObject result)
            {
                message = (string?)result["message"] ?? "";
                var code = result["code"]?.Type == JTokenType.Integer ? (int)result["code"]! : 0;
                return code == 0;
            }
            message = "";
            return true;
        }
    }

    /// <summary>Requests a running <c>flutter run --machine</c> process understands on stdin.</summary>
    public static class FlutterDaemon
    {
        /// <summary>
        /// Hot reload (<paramref name="fullRestart"/> false: injects changed code, keeps state) or hot restart (true:
        /// restarts the app, loses state). <paramref name="reason"/> is reported to analytics: <c>manual</c> or <c>save</c>.
        /// </summary>
        public static string Restart(int id, string appId, bool fullRestart, string reason) =>
            Request(id, "app.restart", new JObject { ["appId"] = appId, ["fullRestart"] = fullRestart, ["pause"] = false, ["reason"] = reason });

        public static string Stop(int id, string appId) => Request(id, "app.stop", new JObject { ["appId"] = appId });

        public static string Detach(int id, string appId) => Request(id, "app.detach", new JObject { ["appId"] = appId });

        private static string Request(int id, string method, JObject parameters) =>
            new JArray(new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters }).ToString(Formatting.None);

        /// <summary>The devices from <c>flutter devices --machine</c>: (id, name, platform), emulators and unsupported ones included.</summary>
        public static IReadOnlyList<FlutterDevice> ParseDevices(string output)
        {
            var result = new List<FlutterDevice>();
            var start = output.IndexOf('[');
            var end = output.LastIndexOf(']');
            if (start < 0 || end <= start) return result;
            JArray array;
            try { array = JArray.Parse(output.Substring(start, end - start + 1)); }
            catch (JsonException) { return result; }
            foreach (var item in array)
            {
                if (!(item is JObject device) || !(device["id"] is JValue id)) continue;
                result.Add(new FlutterDevice((string)id!, (string?)device["name"] ?? (string)id!, (string?)device["targetPlatform"] ?? "",
                    device["isSupported"]?.Type != JTokenType.Boolean || (bool)device["isSupported"]!));
            }
            return result;
        }
    }

    public sealed class FlutterDevice
    {
        public FlutterDevice(string id, string name, string platform, bool isSupported)
        { Id = id; Name = name; Platform = platform; IsSupported = isSupported; }
        public string Id { get; }
        public string Name { get; }
        public string Platform { get; }
        public bool IsSupported { get; }
        public override string ToString() => Name + " (" + Id + (Platform.Length == 0 ? "" : ", " + Platform) + ")";
    }
}
