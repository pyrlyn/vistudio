using System;
using System.Linq;
using Vistudio;

internal static class CompletionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = KotlinCompletionQuery.Parse("? gre", 5)!;
        check(root.Receiver.Length == 0 && root.Start == 2 && root.Length == 3, "Immediate prefix is excluded from replacement");
        var field = KotlinCompletionQuery.Parse("greeting?.na", 12)!;
        check(field.Receiver.SequenceEqual(new[] { "greeting" }) && field.Start == 10 && field.Length == 2, "Safe member access completes only the member name");
        var middle = KotlinCompletionQuery.Parse("greeting.name", 11)!;
        check(middle.Start == 9 && middle.Length == 4, "Completion in the middle replaces the whole identifier");
        var nested = KotlinCompletionQuery.Parse("? `my local`.child.", 19)!;
        check(nested.Receiver.SequenceEqual(new[] { "my local", "child" }) && nested.Length == 0, "Backtick receiver and chained fields preserve context");
        foreach (var input in new[] { "counter.next().", "values[0].na", "\"greeting.na", "// gre", ">Debug.Start", "greeting??.na" })
            check(KotlinCompletionQuery.Parse(input, input.Length) == null, "Unsupported or non-code input is never traversed: " + input);
        check(KotlinCompletionQuery.Insertion("class") == "`class`" && KotlinCompletionQuery.Insertion("my local") == "`my local`", "Kotlin keyword and spaced names use backticks");
        check(KotlinCompletionQuery.Insertion("this") == "this" && KotlinCompletionQuery.Insertion("\u0438\u043c\u044f") == "\u0438\u043c\u044f", "Receiver and Unicode identifier spelling is retained");
        check(KotlinCompletionQuery.JavaInsertion("ledger") == "ledger" && KotlinCompletionQuery.JavaInsertion("this$0") == "this$0" && KotlinCompletionQuery.JavaInsertion("this") == "this", "Java frame names are inserted as-is");
        check(KotlinCompletionQuery.JavaInsertion("fun") == "fun" && KotlinCompletionQuery.JavaInsertion("class") == null && KotlinCompletionQuery.JavaInsertion("my local") == null && KotlinCompletionQuery.JavaInsertion("[0]") == null, "Java keywords and unspellable names are skipped, without Kotlin backticks");
        check(KotlinCompletionQuery.Insertion("$i$f$map") == null && KotlinCompletionQuery.Insertion("[0]") == null, "Synthetic JVM and indexed members are not inserted as Kotlin names");
        check(KotlinCompletionQuery.Parse(new string('a', 2049), 2049) == null, "Long editor lines do not trigger completion traversal");
        check(KotlinCompletionQuery.Parse("a.b.c.d.e.f.g.h.i.", 18) == null, "Deep member traversal is bounded");
    }
}
