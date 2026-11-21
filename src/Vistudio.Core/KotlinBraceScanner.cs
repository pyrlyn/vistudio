using System.Collections.Generic;

namespace Vistudio.Core
{
    public readonly struct ColoredBrace
    {
        public ColoredBrace(int position, int color) { Position = position; Color = color; }
        public int Position { get; }
        public int Color { get; }
    }

    public static class KotlinBraceScanner
    {
        public const int ColorCount = Editor.BracePalette.ColorCount;
        private sealed class Context
        {
            public char Kind;
            public int Depth;
            public int Dollars = 1;
            public Context(char kind, int depth = -1) { Kind = kind; Depth = depth; }
        }

        public static IReadOnlyList<ColoredBrace> Scan(string text)
        {
            var palette = new Editor.BracePalette();
            var contexts = new Stack<Context>();
            contexts.Push(new Context('c'));
            void Open(int position) => palette.Open(position);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i], following = i + 1 < text.Length ? text[i + 1] : '\0';
                var context = contexts.Peek();
                if (context.Kind == 'l') { if (ch == '\n') contexts.Pop(); continue; }
                if (context.Kind == 'b')
                {
                    if (ch == '/' && following == '*') { context.Depth++; i++; }
                    else if (ch == '*' && following == '/') { if (--context.Depth == 0) contexts.Pop(); i++; }
                    continue;
                }
                if (context.Kind == 's' || context.Kind == 'r')
                {
                    if (context.Kind == 's' && ch == '\\') { i++; continue; }
                    if (ch == '$')
                    {
                        int end = i;
                        while (end < text.Length && text[end] == '$') end++;
                        if (end < text.Length && text[end] == '{' && end - i >= context.Dollars)
                        {
                            contexts.Push(new Context('c', palette.Depth)); Open(end); i = end; continue;
                        }
                        i = end - 1; continue;
                    }
                    if (ch == '"')
                    {
                        if (context.Kind == 's') contexts.Pop();
                        else if (i + 2 < text.Length && following == '"' && text[i + 2] == '"') { contexts.Pop(); i += 2; }
                    }
                    else if (ch == '\n' && context.Kind == 's') contexts.Pop();
                    continue;
                }
                if (context.Kind == '\'' || context.Kind == '`')
                {
                    if (ch == '\\' && context.Kind == '\'') { i++; continue; }
                    if (ch == context.Kind || ch == '\n') contexts.Pop();
                    continue;
                }
                if (ch == '/' && following == '/') { contexts.Push(new Context('l')); i++; }
                else if (ch == '/' && following == '*') { contexts.Push(new Context('b', 1)); i++; }
                else if (ch == '"')
                {
                    bool raw = i + 2 < text.Length && following == '"' && text[i + 2] == '"';
                    int dollars = 0;
                    for (int before = i - 1; before >= 0 && text[before] == '$'; before--) dollars++;
                    contexts.Push(new Context(raw ? 'r' : 's') { Dollars = dollars > 1 ? dollars : 1 });
                    if (raw) i += 2;
                }
                else if (ch == '\'' || ch == '`') contexts.Push(new Context(ch));
                else if (ch == '{') Open(i);
                else if (ch == '}' && palette.Depth > 0)
                {
                    palette.Close(i);
                    if (context.Depth >= 0 && palette.Depth == context.Depth) contexts.Pop();
                }
            }
            return palette.Result;
        }
    }
}
