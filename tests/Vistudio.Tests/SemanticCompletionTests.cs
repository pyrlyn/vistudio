using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class SemanticCompletionTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var file = System.IO.Path.GetFullPath("Frame.kt");
        const string source = "package sample\r\nimport sample.extensions.*\r\nfun outer() {\r\n val target: List<String> = listOf(\"x\")\r\n fun List<String>.localOnly() = size\r\n println(target)\r\n}\r\nfun inner() {\r\n val target = listOf(1)\r\n println(target)\r\n}\r\nprivate fun <T> List<T>.privateGeneric(): T? = firstOrNull()\r\n";
        var main = KotlinSemanticCompletion.Create(file, source, 6, "target.bu")!;
        check(main.Text.Contains("fun List<String>.localOnly()") && main.Text.Contains("private fun <T> List<T>.privateGeneric()"), "Lexical snapshot preserves local/private declarations and generic signatures");
        check(main.Text.Contains("import sample.extensions.*") && main.Column == 9 && main.Line == 5, "One-based frame line maps to UTF-16 LSP position with CRLF source");
        var inner = KotlinSemanticCompletion.Create(file, source, 10, "target.")!;
        check(inner.Text.Contains(" val target = listOf(1)\r\ntarget.\n;\n println") && inner.Uri == main.Uri, "Frame snapshots reuse the source identity without accumulating generic owners");
        check(KotlinSemanticCompletion.Create(System.IO.Path.GetFullPath("Other.kt"), source, 6, "target.")!.Uri != main.Uri, "Different source files use distinct virtual identities");
        check(main.Text.Replace("target.bu\n;\n", "") == source && inner.Text.Replace("target.\n;\n", "") == source, "Original context is preserved byte-for-character outside the insertion");
        check(KotlinSemanticCompletion.Create(file, source, 0, "target.") == null && KotlinSemanticCompletion.Create(file, source, 999, "target.") == null, "Invalid frame positions fail closed instead of using another scope");
        check(KotlinSemanticCompletion.Create(file, source, 6, "target.next().") == null && KotlinSemanticCompletion.Create(file, source, 6, "? target.") == null, "Only parsed receiver chains enter the snapshot");
        check(KotlinSemanticCompletion.Create(file, new string('x', 1024 * 1024 + 1), 1, "target.") == null, "Large source snapshots remain bounded");
        const string generic = "class Holder<T : CharSequence>(val items: Map<String, List<out T?>>) {\n fun pause(star: List<*>) {\n println(items)\n }\n}";
        var bounded = KotlinSemanticCompletion.Create(file, generic, 3, "items.")!;
        check(bounded.Text.Contains("T : CharSequence") && bounded.Text.Contains("Map<String, List<out T?>>") && bounded.Text.Contains("List<*>"), "Bounds, nested arguments, nullability, variance and star projections are left to Kotlin analysis");
        var reply = JObject.Parse(@"{ 'items': [
            {'label':'bumpMethod','kind':2,'labelDetails':{'detail':'()','description':'Int'},'command':{'command':'dangerous.apply'}},
            {'label':'bumpExtension','kind':3,'labelDetails':{'detail':'() for CompletionTarget in sample.extensions','description':'Int'}},
            {'label':'hiddenExtension','kind':3,'labelDetails':{'detail':'() for CompletionTarget in not.imported','description':'Int'}},
            {'label':'overload','kind':2,'labelDetails':{'detail':'(value: Int)','description':'Int'}},
            {'label':'overload','kind':2,'labelDetails':{'detail':'(value: String)','description':'String'}},
            {'label':'needsEdit','kind':3,'additionalTextEdits':[{}]},
            {'label':'field','kind':6}, {'label':'bad(); next()','kind':2} ] }");
        var items = main.Decode(reply);
        check(items.Count == 4 && items.Count(x => x.Name == "overload") == 2, "Methods, imported extensions and overload signatures survive filtering");
        var lexical = main.Decode(JObject.Parse(@"{'items':[
            {'label':'localOnly','kind':3,'labelDetails':{'detail':'() for List<String>','description':'Int'}},
            {'label':'privateGeneric','kind':3,'labelDetails':{'detail':'() for List<T> in sample','description':'String?'}},
            {'label':'get','kind':24,'labelDetails':{'detail':'(index: Int)','description':'String'}},
            {'label':'lexMember','kind':2,'labelDetails':{'detail':'() for List<T> in LexicalOwner','description':'T?'}}]}"));
        check(lexical.Count == 4 && lexical[1].Description.EndsWith("String?") && lexical[2].Description.EndsWith("String"), "Local extensions, private generic substitutions and operator signatures are retained");
        check(lexical[3].Description.Contains("in LexicalOwner"), "A private member extension's enclosing class is not mistaken for a missing import");
        check(items.All(x => !x.Insertion.Contains("(")), "Accepting a callable inserts its name without implicitly invoking it");
        var methods = new List<string>();
        var result = await main.RequestAsync((method, args, token) => { methods.Add(method); return Task.FromResult<JToken>(reply); },
            (method, args) => { methods.Add(method); return Task.CompletedTask; }, CancellationToken.None);
        check(methods.SequenceEqual(new[] { "textDocument/didOpen", "textDocument/completion", "textDocument/documentSymbol", "textDocument/didClose" }) && result.Count == 4,
            "Suggestions use only LSP document lifecycle and completion, never evaluate or completion.apply");
        methods.Clear();
        var attempts = 0;
        result = await main.RequestAsync((method, args, token) =>
        {
            methods.Add(method);
            if (method == "textDocument/documentSymbol") return Task.FromResult<JToken>(new JArray());
            attempts++;
            check((int)args["context"]!["triggerKind"]! == (attempts == 1 ? 1 : 3), "Incomplete list requery uses the LSP trigger kind");
            return Task.FromResult<JToken>(attempts == 1 ? JObject.Parse("{'isIncomplete':true,'items':[]}") : reply);
        }, (method, args) => { methods.Add(method); return Task.CompletedTask; }, CancellationToken.None);
        check(result.Count == 4 && methods.SequenceEqual(new[] { "textDocument/didOpen", "textDocument/completion", "textDocument/completion", "textDocument/documentSymbol", "textDocument/didClose" }),
            "An incomplete empty list is requeried in the same open document without executing code");
        attempts = 0;
        await main.RequestAsync((method, args, token) => { if (method == "textDocument/documentSymbol") return Task.FromResult<JToken>(new JArray()); attempts++; return Task.FromResult<JToken>(JObject.Parse("{'isIncomplete':true,'items':[]}")); },
            (method, args) => Task.CompletedTask, CancellationToken.None);
        check(attempts == 4, "Incomplete list retries remain bounded");
        attempts = 0;
        await main.RequestAsync((method, args, token) => { if (method == "textDocument/documentSymbol") return Task.FromResult<JToken>(new JArray()); attempts++; return Task.FromResult<JToken>(JObject.Parse("{'isIncomplete':false,'items':[]}")); },
            (method, args) => Task.CompletedTask, CancellationToken.None);
        check(attempts == 1, "A complete empty list is not retried");
        using (var cancel = new CancellationTokenSource())
        {
            methods.Clear();
            try
            {
                await main.RequestAsync((method, args, token) => { cancel.Cancel(); return Task.FromResult<JToken>(reply); },
                    (method, args) => { methods.Add(method); return Task.CompletedTask; }, cancel.Token);
                check(false, "Canceled completion must not publish stale results");
            }
            catch (OperationCanceledException) { check(methods.Last() == "textDocument/didClose", "Canceled requests still close their scratch document"); }
        }
        methods.Clear();
        try
        {
            await main.RequestAsync((method, args, token) => throw new InvalidOperationException("server failure"),
                (method, args) => { methods.Add(method); return Task.CompletedTask; }, CancellationToken.None);
        }
        catch (InvalidOperationException) { check(methods.Last() == "textDocument/didClose", "Server errors close scratch documents without retrying code"); }
        await MemberExtensionTests.RunAsync(check);
    }
}
