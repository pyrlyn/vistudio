using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Bazel
{
    public enum StarlarkTokenKind { Identifier, String, Number, Open, Close, Comma, Equals, Colon, Dot, Operator, Newline }

    /// <summary>One token of a Starlark file; comments and spaces are skipped, line breaks inside brackets are not reported.</summary>
    public sealed class StarlarkToken
    {
        internal StarlarkToken(StarlarkTokenKind kind, int start, int end, string text, int line, int column)
        {
            Kind = kind; Start = start; End = end; Text = text; Line = line; Column = column;
        }

        public StarlarkTokenKind Kind { get; }
        /// <summary>Offset of the first character (for strings: the prefix or opening quote).</summary>
        public int Start { get; }
        public int End { get; }
        /// <summary>The source text; for strings the decoded value without quotes.</summary>
        public string Text { get; }
        public int Line { get; }
        public int Column { get; }
        /// <summary>Strings: the closing quote was found.</summary>
        public bool Closed { get; internal set; } = true;
        /// <summary>Strings: offset of the first character of the value, after the prefix and opening quotes.</summary>
        public int ValueStart { get; internal set; }

        public override string ToString() => Kind + " " + Text;
    }

    /// <summary>
    /// A small Starlark lexer for BUILD, MODULE.bazel, WORKSPACE and .bzl files: enough for labels, targets, completion
    /// contexts and bracket checks. It never throws; an unterminated string ends at the line end (or the text end for
    /// triple quotes) and is marked not closed.
    /// </summary>
    public static class StarlarkLexer
    {
        public static IReadOnlyList<StarlarkToken> Tokenize(string text)
        {
            var tokens = new List<StarlarkToken>();
            var depth = 0;
            int i = 0, line = 0, lineStart = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\n')
                {
                    if (depth == 0 && tokens.Count != 0 && tokens[tokens.Count - 1].Kind != StarlarkTokenKind.Newline)
                        tokens.Add(new StarlarkToken(StarlarkTokenKind.Newline, i, i + 1, "\n", line, i - lineStart));
                    i++; line++; lineStart = i;
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f') { i++; continue; }
                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r')) { i++; continue; }
                if (c == '#')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                var start = i;
                var column = i - lineStart;
                // String prefixes: r, b, rb, br (any case) directly before a quote.
                var prefixLength = 0;
                while (prefixLength < 2 && i + prefixLength < text.Length && "rRbB".IndexOf(text[i + prefixLength]) >= 0) prefixLength++;
                if (i + prefixLength < text.Length && (text[i + prefixLength] == '"' || text[i + prefixLength] == '\''))
                {
                    var raw = text.Substring(i, prefixLength).IndexOf('r') >= 0 || text.Substring(i, prefixLength).IndexOf('R') >= 0;
                    var token = ReadString(text, i, i + prefixLength, raw, line, column, ref line, ref lineStart);
                    tokens.Add(token);
                    i = token.End;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new StarlarkToken(StarlarkTokenKind.Identifier, start, i, text.Substring(start, i - start), line, column));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                    tokens.Add(new StarlarkToken(StarlarkTokenKind.Number, start, i, text.Substring(start, i - start), line, column));
                    continue;
                }
                StarlarkTokenKind kind;
                var length = 1;
                switch (c)
                {
                    case '(': case '[': case '{': kind = StarlarkTokenKind.Open; depth++; break;
                    case ')': case ']': case '}': kind = StarlarkTokenKind.Close; depth = Math.Max(0, depth - 1); break;
                    case ',': kind = StarlarkTokenKind.Comma; break;
                    case ':': kind = StarlarkTokenKind.Colon; break;
                    case '.': kind = StarlarkTokenKind.Dot; break;
                    case '=':
                        if (i + 1 < text.Length && text[i + 1] == '=') { kind = StarlarkTokenKind.Operator; length = 2; }
                        else kind = StarlarkTokenKind.Equals;
                        break;
                    default:
                        kind = StarlarkTokenKind.Operator;
                        // Two-character operators (+=, -=, !=, <=, >=, **, //, ->) stay one token so '=' is not read as an assignment.
                        if (i + 1 < text.Length && "=*/>".IndexOf(text[i + 1]) >= 0 && "+-*/%<>!&|^".IndexOf(c) >= 0) length = 2;
                        break;
                }
                tokens.Add(new StarlarkToken(kind, start, start + length, text.Substring(start, length), line, column));
                i += length;
            }
            return tokens;
        }

        private static StarlarkToken ReadString(string text, int start, int quoteStart, bool raw, int startLine, int column, ref int line, ref int lineStart)
        {
            var quote = text[quoteStart];
            var triple = quoteStart + 2 < text.Length && text[quoteStart + 1] == quote && text[quoteStart + 2] == quote;
            var i = quoteStart + (triple ? 3 : 1);
            var valueStart = i;
            var value = new StringBuilder();
            var closed = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (!triple && c == '\n') break;
                if (c == '\\' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (raw) { value.Append(c).Append(next); }
                    else
                    {
                        switch (next)
                        {
                            case 'n': value.Append('\n'); break;
                            case 't': value.Append('\t'); break;
                            case 'r': value.Append('\r'); break;
                            case '\n': break;
                            default: value.Append(next); break;
                        }
                    }
                    if (next == '\n') { line++; lineStart = i + 2; }
                    i += 2;
                    continue;
                }
                if (c == quote && (!triple || i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote))
                {
                    i += triple ? 3 : 1;
                    closed = true;
                    break;
                }
                if (c == '\n') { line++; lineStart = i + 1; }
                value.Append(c);
                i++;
            }
            return new StarlarkToken(StarlarkTokenKind.String, start, i, value.ToString(), startLine, column) { Closed = closed, ValueStart = valueStart };
        }
    }

    /// <summary>A top-level call statement such as <c>cc_library(name = "lib", srcs = [...])</c>.</summary>
    public sealed class StarlarkCall
    {
        internal StarlarkCall(StarlarkToken function, StarlarkToken open)
        {
            Function = function;
            Open = open;
        }

        public string Name => Function.Text;
        public StarlarkToken Function { get; }
        public StarlarkToken Open { get; }
        public StarlarkToken? Close { get; internal set; }
        /// <summary>Keyword arguments in order: name, the tokens of the value (outer brackets included).</summary>
        public List<(StarlarkToken Keyword, IReadOnlyList<StarlarkToken> Value)> Keywords { get; } = new List<(StarlarkToken, IReadOnlyList<StarlarkToken>)>();
        /// <summary>Positional arguments: the tokens of each.</summary>
        public List<IReadOnlyList<StarlarkToken>> Positional { get; } = new List<IReadOnlyList<StarlarkToken>>();

        public IReadOnlyList<StarlarkToken>? Argument(string keyword) =>
            Keywords.Where(k => k.Keyword.Text == keyword).Select(k => k.Value).FirstOrDefault();

        /// <summary>The value of a keyword argument when it is one plain string.</summary>
        public StarlarkToken? StringArgument(string keyword)
        {
            var value = Argument(keyword);
            return value != null && value.Count == 1 && value[0].Kind == StarlarkTokenKind.String ? value[0] : null;
        }

        /// <summary>Every string literal in a keyword argument's value (lists, glob, select branches).</summary>
        public IEnumerable<StarlarkToken> Strings(string keyword) =>
            (Argument(keyword) ?? Array.Empty<StarlarkToken>()).Where(t => t.Kind == StarlarkTokenKind.String);
    }

    /// <summary>Top-level statements of a Starlark file: calls, loads, function definitions and assignments.</summary>
    public sealed class StarlarkFile
    {
        private StarlarkFile(IReadOnlyList<StarlarkToken> tokens) { Tokens = tokens; }

        public IReadOnlyList<StarlarkToken> Tokens { get; }
        public List<StarlarkCall> Calls { get; } = new List<StarlarkCall>();
        /// <summary><c>def name(</c> at the start of a line.</summary>
        public List<StarlarkToken> Functions { get; } = new List<StarlarkToken>();
        /// <summary><c>NAME = ...</c> at the top level.</summary>
        public List<StarlarkToken> Globals { get; } = new List<StarlarkToken>();

        public IEnumerable<StarlarkCall> Loads => Calls.Where(c => c.Name == "load");

        /// <summary>Symbols made available by <c>load()</c>: local name (alias or loaded name).</summary>
        public IEnumerable<string> LoadedSymbols => Loads.SelectMany(l =>
            l.Positional.Skip(1).Where(p => p.Count == 1 && p[0].Kind == StarlarkTokenKind.String).Select(p => p[0].Text)
                .Concat(l.Keywords.Select(k => k.Keyword.Text)));

        public static StarlarkFile Parse(string text) => Parse(StarlarkLexer.Tokenize(text));

        public static StarlarkFile Parse(IReadOnlyList<StarlarkToken> tokens)
        {
            var file = new StarlarkFile(tokens);
            var depth = 0;
            var atStatementStart = true;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                switch (token.Kind)
                {
                    case StarlarkTokenKind.Newline:
                        atStatementStart = depth == 0;
                        continue;
                    case StarlarkTokenKind.Open: depth++; break;
                    case StarlarkTokenKind.Close: depth = Math.Max(0, depth - 1); break;
                }
                if (depth == 0 && atStatementStart && token.Kind == StarlarkTokenKind.Identifier)
                {
                    if (token.Text == "def" && token.Column == 0 && i + 1 < tokens.Count && tokens[i + 1].Kind == StarlarkTokenKind.Identifier)
                        file.Functions.Add(tokens[i + 1]);
                    else if (token.Column == 0 && i + 1 < tokens.Count && tokens[i + 1].Kind == StarlarkTokenKind.Equals)
                        file.Globals.Add(token);
                    else if (token.Column == 0 && i + 1 < tokens.Count && tokens[i + 1].Kind == StarlarkTokenKind.Open && tokens[i + 1].Text == "(")
                    {
                        var call = ReadCall(tokens, i, out var end);
                        file.Calls.Add(call);
                        i = end; // the closing parenthesis (or the last token)
                        atStatementStart = false;
                        continue;
                    }
                }
                atStatementStart = false;
            }
            return file;
        }

        private static StarlarkCall ReadCall(IReadOnlyList<StarlarkToken> tokens, int functionIndex, out int end)
        {
            var call = new StarlarkCall(tokens[functionIndex], tokens[functionIndex + 1]);
            var depth = 0;
            var argument = new List<StarlarkToken>();
            StarlarkToken? keyword = null;
            void Flush()
            {
                if (keyword != null) call.Keywords.Add((keyword, argument));
                else if (argument.Count != 0) call.Positional.Add(argument);
                keyword = null;
                argument = new List<StarlarkToken>();
            }
            for (var i = functionIndex + 2; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == StarlarkTokenKind.Newline) continue;
                if (depth == 0 && token.Kind == StarlarkTokenKind.Close)
                {
                    Flush();
                    call.Close = token;
                    end = i;
                    return call;
                }
                if (depth == 0 && token.Kind == StarlarkTokenKind.Comma) { Flush(); continue; }
                if (depth == 0 && argument.Count == 0 && keyword == null && token.Kind == StarlarkTokenKind.Identifier
                    && i + 1 < tokens.Count && tokens[i + 1].Kind == StarlarkTokenKind.Equals)
                {
                    keyword = token;
                    i++;
                    continue;
                }
                if (token.Kind == StarlarkTokenKind.Open) depth++;
                else if (token.Kind == StarlarkTokenKind.Close) depth--;
                argument.Add(token);
            }
            Flush();
            end = tokens.Count - 1;
            return call;
        }
    }
}
