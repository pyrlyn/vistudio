using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static Vistudio.Core.CodeLens.CodeDeclarationScanner;

namespace Vistudio.Core.CodeLens
{
    /// <summary>A Gradle task declared in a Kotlin build script; offsets span the declaration up to the task name.</summary>
    public sealed class GradleScriptTask
    {
        public GradleScriptTask(string name, int startOffset, int endOffset, bool application)
        { Name = name; StartOffset = startOffset; EndOffset = endOffset; IsApplication = application; }
        public string Name { get; }
        public int StartOffset { get; }
        public int EndOffset { get; }
        /// <summary>The <c>application { }</c> block, which stands for the <c>run</c> task of the application plugin.</summary>
        public bool IsApplication { get; }
    }

    /// <summary>
    /// Lexical scanner for tasks declared at the top level of a <c>build.gradle.kts</c>: <c>tasks.register("x")</c>,
    /// <c>tasks.create/named("x")</c>, <c>task("x")</c>, <c>val x by tasks.registering/creating</c>, <c>register/create/named("x")</c>
    /// inside a top-level <c>tasks { }</c> block, and the <c>application { }</c> block (<c>run</c>). Tasks configured inside
    /// other blocks (<c>subprojects { }</c>, <c>allprojects { }</c>) belong to other projects and are skipped.
    /// </summary>
    public static class GradleScriptScanner
    {
        private static readonly Regex TaskName = new Regex(@"\A[A-Za-z_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> CallMethods = new HashSet<string>(StringComparer.Ordinal) { "register", "create", "named" };

        public static bool IsGradleScript(string path) => path.EndsWith(".gradle.kts", StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<GradleScriptTask> Scan(string text)
        {
            var tokens = Tokenize(text, java: false).Where(t => t.Kind != TokenKind.Newline).ToList();
            var result = new List<GradleScriptTask>();
            var blocks = new Stack<string?>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Is("{"))
                {
                    blocks.Push(i > 0 && tokens[i - 1].Kind == TokenKind.Identifier ? tokens[i - 1].Text : null);
                    continue;
                }
                if (token.Is("}")) { if (blocks.Count > 0) blocks.Pop(); continue; }
                if (token.Kind != TokenKind.Identifier) continue;
                var qualified = i > 0 && tokens[i - 1].Is(".");
                var topLevel = blocks.Count == 0;
                var inTasks = blocks.Count == 1 && blocks.Peek() == "tasks";
                GradleScriptTask? task = null;
                if (topLevel && !qualified && token.Text == "tasks" && At(tokens, i + 1, ".") && i + 2 < tokens.Count && CallMethods.Contains(tokens[i + 2].Text))
                    task = Call(text, tokens, i + 3, token.Offset);
                else if (inTasks && !qualified && CallMethods.Contains(token.Text))
                    task = Call(text, tokens, i + 1, token.Offset);
                else if (topLevel && !qualified && token.Text == "task")
                    task = Call(text, tokens, i + 1, token.Offset);
                else if (topLevel && !qualified && token.Text == "val" && i + 5 < tokens.Count && tokens[i + 1].Kind == TokenKind.Identifier
                    && tokens[i + 2].Is("by") && tokens[i + 3].Is("tasks") && tokens[i + 4].Is(".")
                    && (tokens[i + 5].Is("registering") || tokens[i + 5].Is("creating")))
                {
                    var name = tokens[i + 1];
                    if (TaskName.IsMatch(name.Text)) task = new GradleScriptTask(name.Text, token.Offset, name.Offset + name.Text.Length, false);
                }
                else if (topLevel && !qualified && token.Text == "application" && At(tokens, i + 1, "{"))
                    task = new GradleScriptTask("run", token.Offset, token.Offset + token.Text.Length, true);
                if (task != null) result.Add(task);
            }
            return result;
        }

        private static bool At(List<Token> tokens, int index, string text) => index < tokens.Count && tokens[index].Is(text);

        /// <summary><c>[&lt;Type&gt;]("name"</c> after a task method: the name must be a plain string literal.</summary>
        private static GradleScriptTask? Call(string text, List<Token> tokens, int index, int start)
        {
            if (At(tokens, index, "<"))
            {
                var depth = 0;
                for (; index < tokens.Count; index++)
                {
                    if (tokens[index].Is("<")) depth++;
                    else if (tokens[index].Is(">") && --depth == 0) { index++; break; }
                    else if (tokens[index].Is("(") || tokens[index].Is("{")) return null;
                }
            }
            if (!At(tokens, index, "(") || index + 1 >= tokens.Count || tokens[index + 1].Kind != TokenKind.Literal) return null;
            var literal = tokens[index + 1].Offset;
            if (text[literal] != '"' || (literal + 1 < text.Length && text[literal + 1] == '"')) return null; // not a plain "..." string
            var end = literal + 1;
            while (end < text.Length && text[end] != '"')
            {
                if (text[end] == '$' || text[end] == '\\' || text[end] == '\n') return null; // templates and escapes are not plain names
                end++;
            }
            if (end >= text.Length) return null;
            var name = text.Substring(literal + 1, end - literal - 1);
            return TaskName.IsMatch(name) ? new GradleScriptTask(name, start, end + 1, false) : null;
        }

        /// <summary>
        /// The Gradle build root (nearest folder upwards with <c>settings.gradle(.kts)</c>) and the project path of a
        /// build script by the default layout (<c>:</c> for the root, <c>:app:core</c> for <c>app/core/build.gradle.kts</c>).
        /// </summary>
        public static (string Root, string ProjectPath)? ProjectOf(string scriptPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath));
            var segments = new List<string>();
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "settings.gradle.kts")) || File.Exists(Path.Combine(directory, "settings.gradle")))
                {
                    segments.Reverse();
                    return (directory!, ":" + string.Join(":", segments));
                }
                segments.Add(Path.GetFileName(directory));
                directory = Path.GetDirectoryName(directory);
            }
            return null;
        }

        /// <summary>Fully qualified task path: <c>:verifyJvm</c> in the root project, <c>:app:run</c> in a subproject.</summary>
        public static string TaskPath(string projectPath, string task) => projectPath == ":" ? ":" + task : projectPath + ":" + task;
    }
}
