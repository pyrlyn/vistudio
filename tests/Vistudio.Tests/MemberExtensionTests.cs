using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class MemberExtensionTests
{
    private const string Source = "class Owner<T>(val value: T) {\r\n" +
        " private fun T.lexOwner(x: Int): T = this\r\n" +
        " private fun T.lexOwner(x: String): T = this\r\n" +
        " private fun List<T>.lexList(x: Int): List<T> = this\r\n" +
        " private fun String.lexWrong(): String = this\r\n" +
        " fun lexOrdinary() = 1\r\n" +
        " fun pause(items: List<T>) {\r\n println(items)\r\n }\r\n}\r\n" +
        "class Other<T>(val value: T) {\r\n private fun T.lexOther(): T = this\r\n fun pause() {\r\n println(value)\r\n }\r\n}\r\n";

    private static JObject Position(string text, int offset)
    {
        var before = text.Substring(0, offset);
        return new JObject { ["line"] = before.Count(c => c == '\n'), ["character"] = offset - (before.LastIndexOf('\n') + 1) };
    }
    private static JObject Range(string text, int start, int end) => new JObject { ["start"] = Position(text, start), ["end"] = Position(text, end) };
    private static JObject Method(string text, string name, string declaration)
    {
        var start = text.IndexOf(declaration, StringComparison.Ordinal);
        var selected = text.IndexOf(name + "(", start, StringComparison.Ordinal);
        return new JObject { ["name"] = name, ["kind"] = 6, ["range"] = Range(text, start, text.IndexOf('\n', start)),
            ["selectionRange"] = Range(text, selected, selected + name.Length) };
    }
    private static JArray Symbols(string text)
    {
        var second = text.IndexOf("class Other", StringComparison.Ordinal);
        return new JArray(
            new JObject { ["name"] = "Owner", ["kind"] = 5, ["range"] = Range(text, 0, second), ["selectionRange"] = Range(text, 6, 11), ["children"] = new JArray(
                Method(text, "lexOwner", "private fun T.lexOwner(x: Int)"), Method(text, "lexOwner", "private fun T.lexOwner(x: String)"),
                Method(text, "lexList", "private fun List<T>"), Method(text, "lexWrong", "private fun String"), Method(text, "lexOrdinary", "fun lexOrdinary")) },
            new JObject { ["name"] = "Other", ["kind"] = 5, ["range"] = Range(text, second, text.Length), ["selectionRange"] = Range(text, second + 6, second + 11),
                ["children"] = new JArray(Method(text, "lexOther", "private fun T.lexOther")) });
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var file = System.IO.Path.GetFullPath("Owner.kt");
        var document = KotlinSemanticCompletion.Create(file, Source, 8, "value.lex")!;
        var symbols = Symbols(document.Text);
        var frameOffset = document.Text.IndexOf("value.lex\n", StringComparison.Ordinal);
        var candidates = KotlinMemberExtensions.Candidates(symbols, document.Text, frameOffset, "lex");
        check(candidates.SequenceEqual(new[] { "lexOwner", "lexList", "lexWrong" }), "Only enclosing-class extensions are candidates; overloads are queried once and ordinary methods/sibling classes are excluded");
        check(KotlinMemberExtensions.Candidates(symbols, document.Text, frameOffset, "lexL").SequenceEqual(new[] { "lexList" }), "Typed prefix bounds signature probes");
        var otherOffset = document.Text.IndexOf("println(value)", StringComparison.Ordinal);
        check(KotlinMemberExtensions.Candidates(symbols, document.Text, otherOffset, "lex").SequenceEqual(new[] { "lexOther" }), "A different owner with the same T spelling gets its own declarations");
        var malformed = (JArray)symbols.DeepClone();
        malformed[0]["children"]![0]!["selectionRange"]!["start"]!["character"] = 10000;
        malformed[0]["children"]![1]!["name"] = "invented";
        check(!KotlinMemberExtensions.Candidates(malformed, document.Text, frameOffset, "lex").Contains("lexOwner"), "Invalid or mismatched symbol/source ranges cannot invent candidates");
        const string commented = "class Comments<T> {\n private fun T. /* outer /* nested */ trivia */ lexComment(): T = this\n fun pause() {\n println(0)\n }\n}\n";
        var commentSymbols = new JArray(new JObject { ["name"] = "Comments", ["kind"] = 5, ["range"] = Range(commented, 0, commented.Length),
            ["children"] = new JArray(Method(commented, "lexComment", "private fun T.")) });
        check(KotlinMemberExtensions.Candidates(commentSymbols, commented, commented.IndexOf("println", StringComparison.Ordinal), "lex").Single() == "lexComment", "Nested Kotlin comments between receiver dot and name do not hide an extension");
        var many = "class Many<T> {\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => "fun T.candidate" + i + "(): T = this")) + "\nfun pause() { println(0) }\n}\n";
        var manySymbols = new JArray(new JObject { ["name"] = "Many", ["kind"] = 5, ["range"] = Range(many, 0, many.Length),
            ["children"] = new JArray(Enumerable.Range(0, 40).Select(i => Method(many, "candidate" + i, "fun T.candidate" + i + "("))) });
        check(KotlinMemberExtensions.Candidates(manySymbols, many, many.IndexOf("println", StringComparison.Ordinal), "candidate").Count == 32, "A large owner cannot create unbounded signature requests");

        var methods = new List<string>();
        var opened = new Dictionary<string, string>();
        var closed = new List<string>();
        var primary = JObject.Parse("{'items':[{'label':'existing','kind':2},{'label':'lexOwner','kind':2}]}");
        Task Notify(string method, JObject args)
        {
            methods.Add(method);
            var uri = (string)args["textDocument"]!["uri"]!;
            check(uri.Contains(KotlinSemanticCompletion.FilePrefix), "Every analysis update targets only a virtual URI");
            if (method == "textDocument/didOpen")
            {
                var changed = (string)args["textDocument"]!["text"]!;
                opened.Add(uri, changed);
                check(changed.Contains("private fun List<T>.lexList") && changed.Contains("class Other<T>"), "Probes retain enclosing types, declarations and bounds");
            }
            if (method == "textDocument/didClose") closed.Add(uri);
            return Task.CompletedTask;
        }
        Task<JToken> Request(string method, JObject args, CancellationToken token)
        {
            methods.Add(method);
            if (method == "textDocument/completion") return Task.FromResult<JToken>(primary);
            if (method == "textDocument/documentSymbol") return Task.FromResult<JToken>(symbols);
            if (method == "textDocument/rename") return Task.FromResult<JToken>(new JObject
            {
                ["documentChanges"] = new JArray(new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = document.Uri },
                    ["edits"] = new JArray(new JObject { ["range"] = Range(document.Text, 6, 11), ["newText"] = args["newName"] })
                })
            });
            check(method == "textDocument/signatureHelp", "Fallback never executes an evaluator, command, or build request");
            var changed = opened[(string)args["textDocument"]!["uri"]!];
            var line = changed.Split('\n')[document.Line];
            var column = (int)args["position"]!["character"]!;
            check(line.Length == column && line[column - 1] == '(', "Signature help caret follows the opening parenthesis in an analysis-only probe");
            check(changed.StartsWith("class __VistudioOwner_", StringComparison.Ordinal), "Semantic owner rename isolates probe identities without changing the enclosing generic scope");
            return Task.FromResult<JToken>(line.Contains("lexOwner")
                ? JObject.Parse("{'signatures':[{'label':'T.lexOwner(x: Int): T'},{'label':'T.lexOwner(x: String): T'}]}")
                : JObject.Parse("{'signatures':[]}"));
        }
        var result = await document.RequestAsync(Request, Notify, CancellationToken.None);
        check(result.Count == 3 && result.Count(c => c.Name == "lexOwner") == 2 && result.All(c => c.Name != "lexWrong" && c.Name != "lexList"), "Compiler-validated overloads replace incomplete candidates; incompatible receivers stay absent");
        check(result.All(c => !c.Insertion.Contains("(")) && result.All(c => c.Description.StartsWith(c.Name, StringComparison.Ordinal)), "Descriptions remain filterable while commit inserts only the function name");
        check(opened.Count == 4 && closed.Count == 4 && opened.Keys.All(closed.Contains) && methods.Last() == "textDocument/didClose", "Every isolated probe and primary snapshot is closed once");
        check(opened.Where(p => p.Key != document.Uri).Select(p => p.Value.Split('\n')[0]).Distinct().Count() == 3, "Each call resolution uses a distinct semantic owner identity");

        methods.Clear();
        using (var cancel = new CancellationTokenSource())
        {
            try
            {
                await document.RequestAsync(Request, (method, args) => { methods.Add(method); if (method == "textDocument/didOpen" && (string?)args["textDocument"]?["uri"] != document.Uri) cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
                check(false, "Canceled owner probe must not publish");
            }
            catch (OperationCanceledException)
            {
                check(!methods.Contains("textDocument/signatureHelp") && methods.Count(m => m == "textDocument/didClose") == 2, "Cancellation during a probe prevents the request and closes both virtual documents once");
            }
        }
        var retained = await document.RequestAsync((method, args, token) => method == "textDocument/completion" ? Task.FromResult<JToken>(primary) : throw new NotSupportedException(),
            (method, args) => Task.CompletedTask, CancellationToken.None);
        check(retained.Count == 2, "An unsupported optional symbol/signature feature retains ordinary completion");
        check(KotlinMemberExtensions.Decode("lexOwner", JObject.Parse("{'signatures':[{'label':'T.lexOther(): T'},{'label':'T.lexOwner(): T'},{'label':'T.lexOwner(): T'}]}")).Count == 1, "Signature decoding rejects unrelated names and deduplicates labels");
        check(KotlinCompletionIsolation.SameDocument("file:///c%3A/test/Owner.kt", "file:///C:/test/Owner.kt"), "Backend encoded drive URI matches the Windows document identity");
        check(!KotlinCompletionIsolation.SameDocument("file:///c%3A/test/Other.kt", "file:///C:/test/Owner.kt"), "Other documents cannot be treated as the analysis snapshot");
        var owner = KotlinCompletionIsolation.Owners(symbols, document.Text, document.Line).Single();
        JObject Edit(int start, int end, string value) => new JObject { ["range"] = Range(document.Text, start, end), ["newText"] = value };
        JToken Rename(params JObject[] edits) => new JObject { ["changes"] = new JObject { [document.Uri] = new JArray(edits), ["file:///C:/untouched.kt"] = new JArray(Edit(0, 1, "DO NOT APPLY")) } };
        string? Apply(JToken value) => KotlinCompletionIsolation.Apply(document.Text, document.Uri, document.Line, new[] { Tuple.Create(owner, value) });
        check(Apply(Rename(Edit(6, 11, owner.Alias))) == document.Text.Remove(6, 5).Insert(6, owner.Alias), "Only semantic edits to the virtual source enter the analysis string; external edits are ignored");
        check(Apply(Rename(Edit(6, 11, owner.Alias), Edit(7, 10, "overlap"))) == null, "Overlapping rename edits fail closed");
        check(Apply(Rename(Edit(6, 11, owner.Alias), Edit(frameOffset, frameOffset + 5, "moved"))) == null, "Renaming the inserted receiver chain cannot change its completion position");
        check(Apply(Rename(Edit(6, 11, owner.Alias + "\n"))) == null, "A multiline rename cannot change source-frame line mapping");
    }
}
