using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Vistudio.Core.Dart
{
    public enum DartTestOutcome { Running, Passed, Failed, Skipped }

    public sealed class DartTestResult
    {
        internal DartTestResult(int id, string name, string file, int line, int column)
        { Id = id; Name = name; File = file; Line = line; Column = column; }
        public int Id { get; }
        /// <summary>Full name: group names and the test name joined with spaces (what <c>--plain-name</c> matches).</summary>
        public string Name { get; }
        /// <summary>The test file (absolute) and the zero-based position of the <c>test(</c> call; empty/-1 when unknown.</summary>
        public string File { get; }
        public int Line { get; }
        public int Column { get; }
        public DartTestOutcome Outcome { get; internal set; }
        public TimeSpan Duration { get; internal set; }
        /// <summary>Failure messages with stack traces (<c>error</c> events) and printed output.</summary>
        public List<string> Errors { get; } = new List<string>();
        public List<string> Output { get; } = new List<string>();
        internal int StartTime { get; set; }
    }

    /// <summary>
    /// Follows the JSON test protocol of <c>dart test --reporter json</c> and <c>flutter test --machine</c> (one JSON object
    /// per line: <c>suite</c>, <c>group</c>, <c>testStart</c>, <c>print</c>, <c>error</c>, <c>testDone</c>, <c>done</c>) and
    /// turns it into readable Output lines and per-test results. Hidden tests (loading, setUpAll) are not counted.
    /// </summary>
    public sealed class DartTestRun
    {
        private readonly string workingDirectory;
        private readonly Dictionary<int, DartTestResult> tests = new Dictionary<int, DartTestResult>();
        private readonly Dictionary<int, string> suites = new Dictionary<int, string>();
        private readonly HashSet<int> hidden = new HashSet<int>();
        private readonly List<string> loadErrors = new List<string>();

        public DartTestRun(string workingDirectory) => this.workingDirectory = workingDirectory;

        public IReadOnlyList<DartTestResult> Tests => tests.Values.Where(t => !hidden.Contains(t.Id)).OrderBy(t => t.Id).ToList();
        public int Passed => Tests.Count(t => t.Outcome == DartTestOutcome.Passed);
        public int Failed => Tests.Count(t => t.Outcome == DartTestOutcome.Failed);
        public int Skipped => Tests.Count(t => t.Outcome == DartTestOutcome.Skipped);
        /// <summary>Errors outside a visible test: a suite that failed to load or compile.</summary>
        public IReadOnlyList<string> LoadErrors => loadErrors;
        /// <summary>The <c>done</c> event's success flag; null before it arrived.</summary>
        public bool? Success { get; private set; }

        /// <summary>Consumes one stdout line; returns the text to show (null to show nothing).</summary>
        public string? Accept(string line)
        {
            var text = line.Trim();
            // flutter test --machine also prints daemon-style events ([{"event":"test.startedProcess",...}]).
            if (text.StartsWith("[{\"event\":\"test.", StringComparison.Ordinal) && FlutterDaemonMessage.Parse(text) != null) return null;
            if (text.Length == 0 || text[0] != '{') return line.Length == 0 ? null : line;
            JObject message;
            try { message = JObject.Parse(text); }
            catch (JsonException) { return line; }
            switch ((string?)message["type"])
            {
                case "suite":
                    {
                        var suite = message["suite"] as JObject;
                        if (suite?["id"] != null) suites[(int)suite["id"]!] = (string?)suite["path"] ?? "";
                        return null;
                    }
                case "testStart":
                    {
                        if (!(message["test"] is JObject test) || test["id"] == null) return null;
                        var id = (int)test["id"]!;
                        var name = (string?)test["name"] ?? "";
                        // root_url/root_line point into the test file when the declaration came from a helper.
                        var url = (string?)test["root_url"] ?? (string?)test["url"];
                        var row = (int?)test["root_line"] ?? (int?)test["line"];
                        var column = (int?)test["root_column"] ?? (int?)test["column"];
                        var file = url != null ? DartDiagnosticParser.ResolvePath(url, workingDirectory)
                            : test["suiteID"] != null && suites.TryGetValue((int)test["suiteID"]!, out var path) && path.Length > 0 ? DartDiagnosticParser.ResolvePath(path, workingDirectory) : "";
                        tests[id] = new DartTestResult(id, name, file, row.HasValue ? row.Value - 1 : -1, column.HasValue ? column.Value - 1 : -1)
                        { StartTime = (int?)message["time"] ?? 0 };
                        // The runner's synthetic "loading <file>" test has no url and no group.
                        if (name.StartsWith("loading ", StringComparison.Ordinal) && url == null && (test["groupIDs"] as JArray)?.Count == 0) hidden.Add(id);
                        return null;
                    }
                case "print":
                    {
                        var output = (string?)message["message"] ?? "";
                        if (message["testID"] != null && tests.TryGetValue((int)message["testID"]!, out var test)) test.Output.Add(output);
                        return output;
                    }
                case "error":
                    {
                        var error = ((string?)message["error"] ?? "").TrimEnd();
                        var stack = ((string?)message["stackTrace"] ?? "").TrimEnd();
                        var full = stack.Length == 0 ? error : error + Environment.NewLine + stack;
                        if (message["testID"] != null && tests.TryGetValue((int)message["testID"]!, out var test))
                        {
                            test.Errors.Add(full);
                            if (hidden.Contains(test.Id)) { loadErrors.Add(full); hidden.Remove(test.Id); }
                            return Indent((hidden.Contains(test.Id) ? "" : test.Name + ": ") + full);
                        }
                        loadErrors.Add(full);
                        return Indent(full);
                    }
                case "testDone":
                    {
                        if (message["testID"] == null || !tests.TryGetValue((int)message["testID"]!, out var test)) return null;
                        if ((bool?)message["hidden"] == true && test.Errors.Count == 0) { hidden.Add(test.Id); return null; }
                        if ((bool?)message["hidden"] == true) hidden.Remove(test.Id);
                        var result = (string?)message["result"];
                        test.Outcome = (bool?)message["skipped"] == true ? DartTestOutcome.Skipped
                            : result == "success" ? DartTestOutcome.Passed : DartTestOutcome.Failed;
                        test.Duration = TimeSpan.FromMilliseconds(Math.Max(0, ((int?)message["time"] ?? 0) - test.StartTime));
                        var mark = test.Outcome == DartTestOutcome.Passed ? "PASS" : test.Outcome == DartTestOutcome.Skipped ? "SKIP" : "FAIL";
                        return mark + "  " + test.Name + " (" + test.Duration.TotalMilliseconds.ToString("0") + " ms)";
                    }
                case "done":
                    Success = (bool?)message["success"];
                    return null;
                default:
                    return null; // start, allSuites, group, debug
            }
        }

        public string Summary()
        {
            var builder = new StringBuilder();
            builder.Append(Passed).Append(" passed, ").Append(Failed).Append(" failed");
            if (Skipped > 0) builder.Append(", ").Append(Skipped).Append(" skipped");
            if (loadErrors.Count > 0) builder.Append(", ").Append(loadErrors.Count).Append(" load error").Append(loadErrors.Count == 1 ? "" : "s");
            return builder.ToString();
        }

        /// <summary>Error List entries: one per failed test at its declaration, plus compile errors of suites that did not load.</summary>
        public IReadOnlyList<DartDiagnostic> Diagnostics()
        {
            var result = new List<DartDiagnostic>();
            foreach (var test in Tests.Where(t => t.Outcome == DartTestOutcome.Failed))
            {
                var first = test.Errors.Select(e => e.Split('\n')[0].Trim()).FirstOrDefault(e => e.Length > 0) ?? "failed";
                result.Add(new DartDiagnostic(DartDiagnosticSeverity.Error, "Test failed: " + test.Name + ": " + first, test.File, test.Line, Math.Max(0, test.Column)));
            }
            foreach (var error in loadErrors)
                foreach (var line in error.Split('\n'))
                {
                    var diagnostic = DartDiagnosticParser.ParseCompilerLine(line.Trim(), workingDirectory);
                    if (diagnostic != null) result.Add(diagnostic);
                }
            return result;
        }

        private static string Indent(string text) => "    " + text.Replace("\n", "\n    ");
    }
}
