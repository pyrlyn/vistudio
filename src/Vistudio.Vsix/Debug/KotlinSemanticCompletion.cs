using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // A lexical LSP snapshot. It is never written to disk, compiled by Gradle,
    // evaluated in the JVM, or applied to the user's source document.
    internal sealed class KotlinSemanticCompletion
    {
        internal const string FilePrefix = "__VistudioDebugCompletion_";
        private const string Name = @"(?:[\p{L}_][\p{L}\p{Nd}_]*|`[^`\r\n]+`)";
        private static readonly Regex Header = new Regex(@"^\s*(package|import)\s+(" + Name + @"(?:\." + Name + @")*(?:\.\*)?(?:\s+as\s+" + Name + @")?)\s*;?\s*(?://.*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        internal string Uri { get; }
        internal string Text { get; }
        internal int Line { get; }
        internal int Column { get; }
        private readonly HashSet<string> imports;
        private readonly string package;
        private readonly int frameOffset;
        private readonly string receiver, prefix;

        private KotlinSemanticCompletion(string uri, string text, int line, int column, HashSet<string> imports, string package,
            int frameOffset, string receiver, string prefix)
        {
            Uri = uri; Text = text; Line = line; Column = column; this.imports = imports; this.package = package;
            this.frameOffset = frameOffset; this.receiver = receiver; this.prefix = prefix;
        }

        internal static KotlinSemanticCompletion? Create(string file, string source, int frameLine, string chain)
        {
            if (!Path.IsPathRooted(file) || !file.EndsWith(".kt", StringComparison.OrdinalIgnoreCase) ||
                source.Length > 1024 * 1024 || chain.Length > 2048 || frameLine < 1) return null;
            var query = KotlinCompletionQuery.Parse(chain, chain.Length);
            if (query == null || query.ChainStart != 0 || query.Receiver.Length == 0) return null;
            // DTE reports one-based source lines, not a runtime generic type. Preserve
            // every declaration so Kotlin resolves local/private visibility and type
            // substitutions at this exact lexical scope, including inferred types.
            var start = 0;
            for (var line = 1; line < frameLine; line++)
            {
                var newline = source.IndexOf('\n', start);
                if (newline < 0) return null;
                start = newline + 1;
            }
            if (start >= source.Length) return null;
            var imports = new HashSet<string>(StringComparer.Ordinal);
            var package = "";
            var headerSource = Regex.Replace(source, @"/\*[\s\S]*?\*/", "");
            foreach (Match match in Header.Matches(headerSource))
            {
                var value = match.Groups[2].Value;
                if (match.Groups[1].Value == "package") package = value;
                else imports.Add(value);
            }
            string identity;
            using (var hash = SHA256.Create())
                identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant()))).Replace("-", "");
            // Reuse one virtual identity per source file. The server can retain
            // closed-file symbols; fresh class copies on every keystroke accumulate
            // competing generic owners. The language client serializes lifetimes.
            var scratch = Path.Combine(Path.GetDirectoryName(file)!, FilePrefix + identity + ".kt");
            if (File.Exists(scratch)) return null;
            var uri = new Uri(scratch).AbsoluteUri;
            // A separator prevents an incomplete trailing dot from absorbing the
            // original statement. The complete source is restored simply by closing
            // this separate URI; the host-owned original document is never changed.
            var text = source.Insert(start, chain + "\n;\n");
            return new KotlinSemanticCompletion(uri, text, frameLine - 1, chain.Length, imports, package,
                start, chain.Substring(0, query.Start), chain.Substring(query.Start).Trim('`'));
        }

        internal async Task<IReadOnlyList<KotlinCallable>> RequestAsync(
            Func<string, JObject, CancellationToken, Task<JToken>> request,
            Func<string, JObject, Task> notify, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await notify("textDocument/didOpen", new JObject { ["textDocument"] = new JObject
                    { ["uri"] = Uri, ["languageId"] = "kotlin", ["version"] = 1, ["text"] = Text } }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var arguments = new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = Uri },
                    ["position"] = new JObject { ["line"] = Line, ["character"] = Column },
                    ["context"] = new JObject { ["triggerKind"] = 1 }
                };
                var result = await request("textDocument/completion", arguments, token).ConfigureAwait(false);
                // The pinned server can briefly expose an empty, incomplete list
                // while the just-opened lexical context is being analyzed. Requery
                // this same document; never retry an evaluation or reopen source.
                for (var attempt = 0; attempt < 3 && result is JObject list &&
                    (bool?)list["isIncomplete"] == true && list["items"] is JArray items && items.Count == 0; attempt++)
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                    arguments["context"]!["triggerKind"] = 3; // TriggerForIncompleteCompletions
                    result = await request("textDocument/completion", arguments, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                var primary = Decode(result).ToList();
                // Repeated copies of the same generic owner can lose applicable members
                // in this backend. Use semantic rename results to isolate each analysis
                // probe's owner identity, then let Kotlin call resolution check it.
                try
                {
                    var symbols = await request("textDocument/documentSymbol", new JObject
                        { ["textDocument"] = new JObject { ["uri"] = Uri } }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var candidates = KotlinMemberExtensions.Candidates(symbols, Text, frameOffset, prefix);
                    var owners = KotlinCompletionIsolation.Owners(symbols, Text, Line);
                    var renames = new List<Tuple<KotlinCompletionIsolation.Owner, JToken>>();
                    if (candidates.Count > 0 && owners.Count > 0)
                        foreach (var owner in owners)
                        {
                            var edit = await request("textDocument/rename", new JObject
                            {
                                ["textDocument"] = new JObject { ["uri"] = Uri },
                                ["position"] = owner.Position.DeepClone(), ["newName"] = owner.Alias
                            }, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            renames.Add(Tuple.Create(owner, edit));
                        }
                    var isolated = renames.Count == 0 ? null : KotlinCompletionIsolation.Apply(Text, Uri, Line, renames);
                    foreach (var name in isolated == null ? Array.Empty<string>() : candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        var probe = receiver + KotlinCompletionQuery.Insertion(name) + "(";
                        var offset = KotlinCompletionIsolation.Offset(isolated!, new JObject { ["line"] = Line, ["character"] = 0 });
                        var text = isolated!.Remove(offset, Column).Insert(offset, probe);
                        var aliases = owners.ToDictionary(owner => owner.Alias, owner => "__VistudioOwner_" + Guid.NewGuid().ToString("N"));
                        foreach (var alias in aliases) text = text.Replace(alias.Key, alias.Value);
                        var probeUri = Uri.Substring(0, Uri.Length - 3) + "_" + Guid.NewGuid().ToString("N") + ".kt";
                        try
                        {
                            await notify("textDocument/didOpen", new JObject { ["textDocument"] = new JObject
                                { ["uri"] = probeUri, ["languageId"] = "kotlin", ["version"] = 1, ["text"] = text } }).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            var signatures = await request("textDocument/signatureHelp", new JObject
                            {
                                ["textDocument"] = new JObject { ["uri"] = probeUri },
                                ["position"] = new JObject { ["line"] = Line, ["character"] = probe.Length }
                            }, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (signatures["signatures"] is JArray entries)
                                foreach (var entry in entries)
                                {
                                    var label = (string?)entry["label"] ?? "";
                                    foreach (var owner in owners) label = label.Replace(aliases[owner.Alias], owner.Name);
                                    entry["label"] = label;
                                }
                            var resolved = KotlinMemberExtensions.Decode(name, signatures);
                            if (resolved.Count == 0) continue;
                            primary.RemoveAll(item => item.Name == name);
                            primary.InsertRange(0, resolved);
                        }
                        finally
                        {
                            await notify("textDocument/didClose", new JObject { ["textDocument"] = new JObject { ["uri"] = probeUri } }).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                // Optional language features may be absent on another LSP backend.
                // Preserve the ordinary completion results in that case.
                catch (Exception) { token.ThrowIfCancellationRequested(); }
                return primary.Take(256).ToArray();
            }
            finally
            {
                // Cleanup is independent of request cancellation, including after a timeout.
                await notify("textDocument/didClose", new JObject { ["textDocument"] = new JObject { ["uri"] = Uri } }).ConfigureAwait(false);
            }
        }

        internal IReadOnlyList<KotlinCallable> Decode(JToken response)
        {
            var result = new List<KotlinCallable>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in (response as JArray ?? response["items"] as JArray ?? new JArray()).Take(1024))
            {
                var label = (string?)item["label"] ?? "";
                var detail = (string?)item["labelDetails"]?["detail"] ?? (string?)item["detail"] ?? "";
                var kind = (int?)item["kind"] ?? 0;
                // The pinned server also uses 22 for inherited methods and 24 for operators such as List.get.
                if (kind != 2 && kind != 3 && kind != 22 && kind != 24) continue;
                var insertion = KotlinCompletionQuery.Insertion(label);
                if (insertion == null || label.Length > 256 || !Regex.IsMatch(label, "^" + Name + "$")) continue;
                // Never run the backend's completion.apply command or apply workspace edits.
                // Extensions requiring an additional import are not valid in Watch.
                var origin = Regex.Match(detail, @"\s+in\s+([\p{L}_][\p{L}\p{Nd}_.]*)\s*$");
                // Member extensions are returned as Method (2), with the enclosing
                // class as origin rather than an importable package. Kotlin already
                // checked the dispatch receiver and private visibility for those.
                if (kind == 3 && origin.Success && !IsImported(origin.Groups[1].Value, label)) continue;
                if (item["additionalTextEdits"] is JArray edits && edits.Count != 0) continue;
                var description = label + detail + " : " + ((string?)item["labelDetails"]?["description"] ?? "");
                if (seen.Add(description)) result.Add(new KotlinCallable(label, insertion, description));
                if (result.Count >= 256) break;
            }
            return result;
        }

        private bool IsImported(string origin, string name) => origin == package || origin == "kotlin" || origin == "kotlin.collections" ||
            origin == "kotlin.comparisons" || origin == "kotlin.io" || origin == "kotlin.ranges" || origin == "kotlin.sequences" ||
            origin == "kotlin.text" || origin == "kotlin.jvm" || imports.Contains(origin + ".*") || imports.Contains(origin + "." + name);
    }

    internal sealed class KotlinCallable
    {
        internal string Name { get; }
        internal string Insertion { get; }
        internal string Description { get; }
        internal KotlinCallable(string name, string insertion, string description)
        { Name = name; Insertion = insertion; Description = description; }
    }
}
