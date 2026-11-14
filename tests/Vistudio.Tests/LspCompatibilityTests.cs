using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class LspCompatibilityTests
{
    internal static void Run(Action<bool, string> check)
    {
        var original = JObject.Parse(@"{
          'rootUri': 'file:///C:/project', 'processId': 42,
          'capabilities': {'textDocument': {
            'definition': {'dynamicRegistration': true}, 'references': {'dynamicRegistration': true},
            'completion': {'completionItemKind': {'valueSet': [0,1,2,25,118115]}},
            'publishDiagnostics': {'tagSupport': {'valueSet': [1,2,2147483646]}}
          }}}");
        var normalized = KotlinLspCompatibility.NormalizeInitialize(original);
        check(normalized.SelectToken("capabilities.textDocument.completion.completionItemKind.valueSet")!.Values<int>().SequenceEqual(new[] { 1, 2, 25 }), "Standard completion enum values only");
        check(normalized.SelectToken("capabilities.textDocument.publishDiagnostics.tagSupport.valueSet")!.Values<int>().SequenceEqual(new[] { 1, 2 }), "Standard diagnostic tags only");
        check(original.SelectToken("capabilities.textDocument.completion.completionItemKind.valueSet")!.Count() == 5, "Initialize normalization does not mutate host capabilities");
        check(JToken.DeepEquals(normalized["capabilities"]!["textDocument"]!["definition"], original["capabilities"]!["textDocument"]!["definition"]) &&
            JToken.DeepEquals(normalized["capabilities"]!["textDocument"]!["references"], original["capabilities"]!["textDocument"]!["references"]), "Navigation capabilities preserved");
        check((string?)normalized["rootUri"] == "file:///C:/project" && (int?)normalized["processId"] == 42, "Initialize workspace and process preserved");
        check(JToken.DeepEquals(KotlinLspCompatibility.NormalizeInitialize(normalized), normalized), "Normalization is idempotent");
        check(JToken.DeepEquals(KotlinLspCompatibility.NormalizeInitialize(new JObject()), new JObject()), "Optional capabilities may be absent");
    }

    internal static async Task RunStreamAsync(Action<bool, string> check)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"rootUri\":\"file:///C:/\u03b1\",\"capabilities\":{\"textDocument\":{\"completion\":{\"completionItemKind\":{\"valueSet\":[0,1,25,118115]}}}}}}";
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n");
        var frame = header.Concat(body).ToArray();
        var tail = Encoding.UTF8.GetBytes("Content-Length: 2\r\n\r\n{}");
        using (var output = new MemoryStream())
        using (var adapter = new KotlinInitializeStream(output))
        {
            for (var i = 0; i < frame.Length; i++) await adapter.WriteAsync(frame, i, 1, CancellationToken.None);
            await adapter.FlushAsync(CancellationToken.None);
            var actual = output.ToArray();
            var separator = Encoding.UTF8.GetString(actual).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var length = int.Parse(Encoding.ASCII.GetString(actual, 0, separator).Split(':')[1]);
            check(actual.Length - separator - 4 == length, "Rewritten frame uses UTF-8 byte length after fragmented writes");
            var message = JObject.Parse(Encoding.UTF8.GetString(actual, separator + 4, length));
            check((int?)message["id"] == 7 && (string?)message["params"]!["rootUri"] == "file:///C:/\u03b1", "Framing preserves request ID and Unicode");
            check(message.SelectToken("params.capabilities.textDocument.completion.completionItemKind.valueSet")!.Values<int>().SequenceEqual(new[] { 1, 25 }), "First frame capabilities normalized");
            adapter.Write(tail, 0, tail.Length);
            check(output.ToArray().Skip(actual.Length).SequenceEqual(tail), "Subsequent frames pass through byte-for-byte");
        }
        using (var output = new MemoryStream())
        using (var adapter = new KotlinInitializeStream(output))
        {
            var combined = frame.Concat(tail).ToArray();
            adapter.Write(combined, 0, combined.Length);
            check(output.ToArray().Reverse().Take(tail.Length).Reverse().SequenceEqual(tail), "Coalesced frames preserve the following message");
        }
        foreach (var invalid in new[] { "Content-Length: -1\r\n\r\n", "Content-Length: 4194305\r\n\r\n", "Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}", "X: 2\r\n\r\n", new string('x', 16384) })
        {
            using (var output = new MemoryStream())
            using (var adapter = new KotlinInitializeStream(output))
            {
                var rejected = false;
                try { var bytes = Encoding.ASCII.GetBytes(invalid); adapter.Write(bytes, 0, bytes.Length); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected && output.Length == 0, "Reject invalid initialization framing without sending partial data");
            }
        }
    }
}
