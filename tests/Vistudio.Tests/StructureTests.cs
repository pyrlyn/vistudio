using System;
using System.Linq;
using System.Text.RegularExpressions;
using Vistudio.Core.Structure;

internal static class StructureTests
{
    private const string Kotlin = @"package sample

import kotlin.math.max

/** class InComment { */
@Suppress(""unused"")
data class Box<T>(
    val value: T,
    val label: String = ""{""
) : Comparable<Box<T>>,
    Iterable<T> {
    companion object {
        fun empty(): Box<Int> =
            Box(0)
    }

    @Deprecated(""x"")
    override fun compareTo(other: Box<T>): Int {
        if (label.isEmpty()) {
            return 0
        } else {
            return when (other.label) {
                ""a"" -> {
                    1
                }
                else -> 2
            }
        }
    }

    val size: Int
        get() {
            return 1
        }

    fun walk(items: List<Int>) {
        items
            .filter { it > 0 }
            .forEach { item ->
                println(""${item}"")
                Row(item) {
                    println()
                }
            }
        for (i in 0..3) {
            println(i)
        }
        try {
            run({
                println()
            })
        } catch (e: Exception) {
            println(e)
        }
    }

    override fun iterator(): Iterator<T> = object : Iterator<T> {
        override fun hasNext() = false
        override fun next(): T = value
    }
    fun oneLine() { }
}

enum class Color { RED, GREEN }

fun main()
{
    val text = """"""
        {
        """"""
    println(text)
}
";

    private const string Java = @"package sample;

import java.util.List;

/** Javadoc { */
@SuppressWarnings(""unused"")
public final class Ledger
        extends Base
        implements Runnable {
    static {
        System.out.println(""{"");
    }

    @Override
    public void run() {
        while (true) {
            break;
        }
        Runnable r = new Runnable() {
            @Override
            public void run() {
                System.out.println();
            }
        };
        switch (1) {
            case 1: {
                break;
            }
        }
    }

    public static <T> List<T> copy(
            List<T> items,
            int count) throws Exception {
        return items;
    }

    enum Kind {
        A, B
    }

    record Point(int x, int y) {
        Point {
            if (x < 0) throw new IllegalArgumentException();
        }
    }
}
";

    internal static void Run(Action<bool, string> check)
    {
        var kotlin = Describe(Kotlin, CodeStructureScanner.Scan("Box.kt", Kotlin));
        Expect(check, kotlin, "Type data class Box<T>( val value: T, val label: String = \"{\" ) : Comparable<Box<T>>, Iterable<T>", "Kotlin multi-line class header starts at `data class`, not at annotations or the brace line");
        Expect(check, kotlin, "Type companion object", "Kotlin companion object");
        Expect(check, kotlin, "Member override fun compareTo(other: Box<T>): Int", "Kotlin function after an annotation line");
        Expect(check, kotlin, "Conditional if (label.isEmpty())", "Kotlin if");
        Expect(check, kotlin, "Conditional else", "Kotlin else after a closing brace");
        Expect(check, kotlin, "Conditional return when (other.label)", "Kotlin when");
        Expect(check, kotlin, "Conditional \"a\" ->", "Kotlin when branch");
        Expect(check, kotlin, "Member get()", "Kotlin property accessor");
        Expect(check, kotlin, "Member fun walk(items: List<Int>)", "Kotlin function");
        Expect(check, kotlin, "Expression items .filter { it > 0 } .forEach", "Kotlin call chain continues across lines");
        Expect(check, kotlin, "Loop for (i in 0..3)", "Kotlin for");
        Expect(check, kotlin, "Expression Row(item)", "Kotlin statement after lambda parameters starts its own header");
        Expect(check, kotlin, "Statement try", "Kotlin try");
        Expect(check, kotlin, "Statement catch (e: Exception)", "Kotlin catch");
        Expect(check, kotlin, "Member override fun iterator(): Iterator<T> = object : Iterator<T>", "Kotlin expression body with an object");
        Expect(check, kotlin, "Member fun main()", "Kotlin Allman brace keeps the header line");
        check(!kotlin.Any(x => x.Contains("InComment")), "Kotlin comments have no blocks");
        check(!kotlin.Any(x => x.Contains("oneLine")), "Kotlin single-line blocks are skipped");
        check(!kotlin.Any(x => x.Contains("Color")), "Kotlin single-line enum is skipped");
        check(!kotlin.Any(x => x.Contains("filter") && !x.Contains("forEach")), "Kotlin single-line lambda is skipped");
        check(!kotlin.Any(x => x.EndsWith(" ") || x.Split(' ').Length == 1), "Kotlin bare blocks without a header are skipped: " + string.Join(" | ", kotlin));
        check(kotlin.Count(x => x.StartsWith("Member fun main")) == 1 && !kotlin.Any(x => x.Contains("text")), "Kotlin raw string braces are ignored");

        var blocks = CodeStructureScanner.Scan("Box.kt", Kotlin);
        var box = blocks[0];
        check(box.Kind == CodeBlockKind.Type && Kotlin.Substring(box.HeaderStart, 10) == "data class" && Kotlin[box.OpenBrace] == '{' && Kotlin[box.CloseBrace] == '}', "Kotlin block offsets");
        check(box.HeaderLine == 6 && box.EndLine == Kotlin.Substring(0, box.CloseBrace).Count(c => c == '\n'), "Kotlin block lines");
        check(blocks.Zip(blocks.Skip(1), (a, b) => a.HeaderStart <= b.HeaderStart).All(x => x), "Blocks are sorted by header start");

        var java = Describe(Java, CodeStructureScanner.Scan("Ledger.java", Java));
        Expect(check, java, "Type public final class Ledger extends Base implements Runnable", "Java multi-line class header without annotations");
        Expect(check, java, "Member static", "Java static initializer");
        Expect(check, java, "Member public void run()", "Java method after an annotation line");
        Expect(check, java, "Loop while (true)", "Java while");
        Expect(check, java, "Type Runnable r = new Runnable()", "Java anonymous class");
        Expect(check, java, "Conditional switch (1)", "Java switch");
        Expect(check, java, "Conditional case 1:", "Java case block");
        Expect(check, java, "Member public static <T> List<T> copy( List<T> items, int count) throws Exception", "Java multi-line method header");
        Expect(check, java, "Type enum Kind", "Java enum");
        Expect(check, java, "Type record Point(int x, int y)", "Java record");
        Expect(check, java, "Member Point", "Java compact constructor");
        check(java.Count(x => x == "Member public void run()") == 2, "Java method inside an anonymous class is a member");
        check(!java.Any(x => x.Contains("Javadoc")), "Java comments have no blocks");

        var broken = CodeStructureScanner.Scan("Broken.kt", "class A {\n    fun f() {\n        if (x) {\n}\n");
        check(broken.Count == 1 && broken[0].Kind == CodeBlockKind.Conditional, "Unclosed blocks are dropped, closed ones stay");
        check(CodeStructureScanner.Scan("Empty.kt", "}\n}\n{").Count == 0, "Unmatched braces are tolerated");
        const string SameLine = "fun f() {\n    run { items.map {\n        it\n    } }\n}\n";
        var sameLine = Describe(SameLine, CodeStructureScanner.Scan("Same.kt", SameLine));
        check(sameLine.Length == 2 && sameLine[1] == "Expression run", "A block whose header shares a line with an enclosing header is dropped: " + string.Join(" | ", sameLine));
    }

    private static string[] Describe(string text, System.Collections.Generic.IReadOnlyList<CodeBlock> blocks) =>
        blocks.Select(b => b.Kind + " " + Regex.Replace(text.Substring(b.HeaderStart, b.HeaderEnd - b.HeaderStart), @"\s+", " ")).ToArray();

    private static void Expect(Action<bool, string> check, string[] blocks, string expected, string what) =>
        check(blocks.Contains(expected), what + ": expected `" + expected + "` in " + string.Join(" | ", blocks));
}
