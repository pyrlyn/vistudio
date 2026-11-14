using System;
using System.Linq;
using Vistudio.Core.CodeLens;

internal static class CodeLensTests
{
    private const string Kotlin = @"@file:JvmName(""Tools"")
package sample.lens

import kotlin.math.max

/** class NotInComment */
interface Shape {
    val area: Double
    fun describe(): String = ""shape""
    private fun hidden() {}
}

sealed interface Event
abstract class Base<T>(val id: T) : Shape {
    abstract fun render(): String
    open val label: String get() = ""base""
    fun plain() {
        val local = 1
        fun localFun() {}
        class LocalClass
    }
    constructor(id: T, extra: Int) : this(id)
    companion object {
        const val LIMIT = 3
        fun create(): Base<Int>? = null
    }
}

data class Circle(val radius: Double) : Base<Int>(1) {
    override val area: Double get() = 3.14 * radius * radius
    override fun render() = ""${radius.let { r -> ""{$r}"" }} circle""
    inner class Part
}

enum class Color(val rgb: Int) {
    RED(0xFF0000) { override fun hex() = ""red"" },
    GREEN(0x00FF00);
    open fun hex(): String = ""?""
}

object Registry { val items = mutableListOf<String>() }

fun interface Action { fun run() }

val instance = object : Shape { override val area = 0.0 }

private val text = """"""
    class InRawString { fun nope() }
""""""

fun <T> List<T>.second(): T = this[1]
val String.shout: String get() = uppercase()
fun `with spaces`() = Unit

suspend fun main(args: Array<String>) {
    println(Circle(1.0).render())
}
";

    private const string Java = @"package sample.lens;

import java.util.List;

/** class NotInComment */
@FunctionalInterface
public interface Greeter {
    String greet(String name);
    default String twice(String name) { return greet(name) + greet(name); }
    static Greeter english() { return n -> ""Hello "" + n; }
}

abstract class Animal implements Comparable<Animal> {
    protected final String name;
    private int legs = 4, eyes = 2;
    static final String[] KINDS = { ""cat"", ""dog"" };

    Animal(String name) { this.name = name; }

    public abstract String sound();

    @Override
    public int compareTo(Animal other) {
        class Local { void inner() {} }
        Runnable r = new Runnable() { public void run() {} };
        return name.compareTo(other.name);
    }

    public <T extends Comparable<T>> List<T> sorted(List<T> items) { return items; }

    static class Nested { int value; }
}

enum Mood {
    HAPPY(""h""), SAD(""s"") { @Override String code() { return ""S""; } };
    private final String c;
    Mood(String c) { this.c = c; }
    String code() { return c; }
}

record Point(int x, int y) {}

public class App {
    String text = """"""
        class InTextBlock { void nope() {} }
        """""";
    public static void main(String[] args) {
        System.out.println(new Point(1, 2));
    }
}
";

    internal static void Run(Action<bool, string> check)
    {
        var kotlin = CodeDeclarationScanner.ScanKotlin(Kotlin);
        string Describe(System.Collections.Generic.IEnumerable<CodeDeclaration> items) => string.Join(", ", items.Select(x => x.ToString()));
        var names = kotlin.Select(x => x.QualifiedName).ToList();
        string[] expected =
        {
            "Shape", "Shape.area", "Shape.describe", "Shape.hidden", "Event", "Base", "Base.render", "Base.label", "Base.plain",
            "Base.Base", "Companion.LIMIT", "Companion.create", "Circle", "Circle.area", "Circle.render", "Circle.Part",
            "Color", "Color.hex", "Registry", "Registry.items", "Action", "Action.run", "instance", "text", "second",
            "shout", "with spaces", "main",
        };
        check(names.SequenceEqual(expected.Select(x => x.StartsWith("Companion.") ? "Base." + x : x)), "Kotlin declarations: " + Describe(kotlin));
        CodeDeclaration K(string qualified) => kotlin.Single(x => x.QualifiedName == qualified);
        check(K("Shape").Kind == CodeDeclarationKind.Interface && K("Shape").CanBeImplemented, "Kotlin interface is implementable.");
        check(K("Shape.area").CanBeImplemented && K("Shape.describe").CanBeImplemented && !K("Shape.hidden").CanBeImplemented, "Kotlin interface members are implementable unless private.");
        check(K("Event").CanBeImplemented && K("Base").CanBeImplemented && !K("Circle").CanBeImplemented, "Sealed/abstract classes are implementable; final classes are not.");
        check(K("Base.render").CanBeImplemented && K("Base.label").CanBeImplemented && !K("Base.plain").CanBeImplemented, "Abstract/open members are implementable.");
        check(K("Base.Base").Kind == CodeDeclarationKind.Constructor && K("Base.Base").NameLine == 21, "Secondary constructor: " + K("Base.Base"));
        check(K("Color").Kind == CodeDeclarationKind.Enum && K("Registry").Kind == CodeDeclarationKind.Object && K("Action").Kind == CodeDeclarationKind.Interface, "Kotlin type kinds.");
        check(!K("Circle.area").CanBeImplemented, "Override without open is not reported as implementable.");
        check(K("main").IsMain && kotlin.Count(x => x.IsMain) == 1, "Kotlin top-level main is an entry point.");
        var shape = K("Shape");
        check(shape.NameLine == 6 && shape.NameColumn == 10 && Kotlin.Substring(shape.NameOffset, 5) == "Shape", "Kotlin name position: " + shape);
        check(Kotlin.Substring(K("Shape.area").StartOffset, 3) == "val", "Declaration start is its first keyword.");
        check(Kotlin.Substring(K("Action").StartOffset, 3) == "fun", "fun interface starts at fun.");
        check(K("with spaces").NameColumn == 4, "Backticked names keep their position.");
        const string annotated = "@Deprecated(\"x\")\n@JvmStatic fun f() {}";
        var f = CodeDeclarationScanner.ScanKotlin(annotated).Single();
        check(f.StartOffset == 0 && f.Name == "f" && f.NameLine == 1, "Annotations start the declaration: " + f);

        var java = CodeDeclarationScanner.ScanJava(Java);
        var javaNames = java.Select(x => x.QualifiedName).ToList();
        string[] javaExpected =
        {
            "Greeter", "Greeter.greet", "Greeter.twice", "Greeter.english", "Animal", "Animal.name", "Animal.legs", "Animal.KINDS",
            "Animal.Animal", "Animal.sound", "Animal.compareTo", "Animal.sorted", "Animal.Nested", "Animal.Nested.value",
            "Mood", "Mood.HAPPY", "Mood.SAD", "Mood.c", "Mood.Mood", "Mood.code", "Point", "App", "App.text", "App.main",
        };
        check(javaNames.SequenceEqual(javaExpected), "Java declarations: " + Describe(java));
        CodeDeclaration J(string qualified) => java.Single(x => x.QualifiedName == qualified);
        check(J("Greeter").CanBeImplemented && J("Greeter.greet").CanBeImplemented && !J("Greeter.twice").CanBeImplemented && !J("Greeter.english").CanBeImplemented, "Java interface: abstract methods only.");
        check(J("Animal").CanBeImplemented && J("Animal.sound").CanBeImplemented && !J("Animal.compareTo").CanBeImplemented, "Java abstract class and method.");
        check(J("Animal.Animal").Kind == CodeDeclarationKind.Constructor && J("Mood.Mood").Kind == CodeDeclarationKind.Constructor, "Java constructors.");
        check(J("Animal.legs").Kind == CodeDeclarationKind.Property && J("Animal.sorted").Kind == CodeDeclarationKind.Function, "Java fields and generic methods.");
        check(J("App.main").IsMain && java.Count(x => x.IsMain) == 1, "Java main is an entry point.");
        check(Java.Substring(J("Greeter").StartOffset, 20) == "@FunctionalInterface", "Java annotations start the declaration.");
        check(Java.Substring(J("Animal.compareTo").StartOffset, 9) == "@Override", "Java member annotations start the declaration.");

        check(CodeDeclarationScanner.Scan("A.java", Java).Count == java.Count && CodeDeclarationScanner.Scan("a.kt", Kotlin).Count == kotlin.Count, "Scan dispatches by extension.");
        check(CodeDeclarationScanner.IsSupportedFile("x.KT") && CodeDeclarationScanner.IsSupportedFile("X.java") && !CodeDeclarationScanner.IsSupportedFile("x.kts"), "Supported files.");
        check(CodeDeclarationScanner.ScanKotlin("class A {\n  fun broken(\n").Select(x => x.Name).SequenceEqual(new[] { "A", "broken" }), "Broken code still yields declarations.");
        check(CodeDeclarationScanner.ScanKotlin("val s = \"unterminated\nclass After").Select(x => x.Name).SequenceEqual(new[] { "s", "After" }), "Unterminated strings end at the line.");

        RunKotlinEdgeCases(check);
        RunJavaEdgeCases(check);
        RunElementChecks(check, kotlin, java);
    }

    private static void RunElementChecks(Action<bool, string> check, System.Collections.Generic.IReadOnlyList<CodeDeclaration> kotlin, System.Collections.Generic.IReadOnlyList<CodeDeclaration> java)
    {
        foreach (var declarations in new[] { kotlin, java, CodeDeclarationScanner.ScanJava("class A {\n    A() {}\n    A(int x) {}\n    void f() {}\n    void f(int x) {}\n    void f(String s) {}\n}") })
        {
            var ids = CodeLensElement.Identifiers(declarations);
            check(ids.Count == declarations.Count && ids.Distinct().Count() == ids.Count, "Code element identifiers are unique: " + string.Join(", ", ids));
            for (var i = 0; i < ids.Count; i++)
            {
                var parsed = CodeLensElement.TryParse(ids[i]);
                if (parsed == null || parsed.CanBeImplemented != declarations[i].CanBeImplemented || parsed.IsMain != declarations[i].IsMain)
                {
                    check(false, "Identifier round trip: " + ids[i] + " for " + declarations[i]);
                    break;
                }
            }
        }
        var overloads = CodeLensElement.Identifiers(CodeDeclarationScanner.ScanJava("class A {\n    void f() {}\n    void f(int x) {}\n    void f(String s) {}\n}"));
        check(overloads[1] == "vistudio|--|Function|A.f" && overloads[2] == "vistudio|--|Function|A.f#1" && overloads[3] == "vistudio|--|Function|A.f#2", "Overload ordinals: " + string.Join(", ", overloads));
        var main = CodeLensElement.TryParse(CodeLensElement.Identifiers(CodeDeclarationScanner.ScanKotlin("fun main() {}"))[0]);
        check(main != null && main.IsMain && !main.CanBeImplemented, "Entry point flag survives the identifier.");
        var iface = CodeLensElement.TryParse(CodeLensElement.Identifiers(CodeDeclarationScanner.ScanKotlin("interface I"))[0]);
        check(iface != null && iface.CanBeImplemented && !iface.IsMain, "Implementable flag survives the identifier.");
        check(CodeLensElement.TryParse(null) == null && CodeLensElement.TryParse("") == null && CodeLensElement.TryParse("vistudio|") == null
            && CodeLensElement.TryParse("vistudio|IMx") == null && CodeLensElement.TryParse("roslyn|IM|Class|A") == null && CodeLensElement.TryParse("M:Namespace.Type.Method") == null,
            "Foreign or malformed identifiers are ignored.");

        // The lens queries the server at the declaration name's last character.
        const string source = "package p\n\n@Deprecated(\"x\")\nclass Greeting {\n    fun `say hi`() {}\n}\n";
        var declared = CodeDeclarationScanner.ScanKotlin(source);
        var greeting = declared.Single(x => x.Name == "Greeting");
        check(CodeLensElement.NamePosition(source, greeting.StartOffset, greeting.EndOffset) == (3, 13), "Name position spans the annotation line: " + CodeLensElement.NamePosition(source, greeting.StartOffset, greeting.EndOffset));
        var sayHi = declared.Single(x => x.Name == "say hi");
        var tickPosition = CodeLensElement.NamePosition(source, sayHi.StartOffset, sayHi.EndOffset);
        check(tickPosition == (4, 14) && source.Split('\n')[4][tickPosition.Character] == 'i', "Backticked name position is inside the backticks: " + tickPosition);
        var crlf = "class A {\r\n    fun f() {}\r\n}";
        var f = CodeDeclarationScanner.ScanKotlin(crlf).Single(x => x.Name == "f");
        check(CodeLensElement.NamePosition(crlf, f.StartOffset, f.EndOffset) == (1, 8), "CRLF name position.");
        check(CodeLensElement.NamePosition("ab", 5, 9) == (0, 2) && CodeLensElement.NamePosition("", 0, 0) == (0, 0), "Ranges past the end are clamped.");
        foreach (var d in declared.Concat(java))
        {
            var text = declared.Contains(d) ? source : Java;
            var position = CodeLensElement.NamePosition(text, d.StartOffset, d.EndOffset);
            if (position.Line != d.NameLine || position.Character < d.NameColumn || position.Character >= d.NameColumn + d.NameLength)
            {
                check(false, "Name position falls inside the name: " + d + " -> " + position);
                break;
            }
        }

        check(CodeLensElement.CountLabel(0, "reference") == "0 references" && CodeLensElement.CountLabel(1, "reference") == "1 reference"
            && CodeLensElement.CountLabel(3, "implementation") == "3 implementations", "Count labels.");
    }

    private static string Names(System.Collections.Generic.IEnumerable<CodeDeclaration> items) => string.Join(", ", items.Select(x => x.QualifiedName));

    private static void ExpectKotlin(Action<bool, string> check, string source, string expected, string what)
    {
        var actual = Names(CodeDeclarationScanner.ScanKotlin(source));
        check(actual == expected, what + ": expected [" + expected + "], got [" + actual + "]");
    }

    private static void ExpectJava(Action<bool, string> check, string source, string expected, string what)
    {
        var actual = Names(CodeDeclarationScanner.ScanJava(source));
        check(actual == expected, what + ": expected [" + expected + "], got [" + actual + "]");
    }

    private static void RunKotlinEdgeCases(Action<bool, string> check)
    {
        ExpectKotlin(check, "class A(\n    val x: Int,\n    private val y: String = \"}\",\n) : Base(),\n    Comparable<A> {\n    fun f() {}\n}\nfun g() {}",
            "A, A.f, g", "Multi-line class header keeps the body as members");
        ExpectKotlin(check, "class A {\n    fun f() = run {\n        val local = 1\n        local\n    }\n    val p = listOf(1)\n        .map { it + 1 }\n    fun g() {}\n}",
            "A, A.f, A.p, A.g", "Expression bodies with lambdas and call chains");
        ExpectKotlin(check, "class A {\n    val x: Int\n        get() = 1\n    var y = 0\n        private set\n    init {\n        val notMember = 2\n    }\n    fun z() {}\n}",
            "A, A.x, A.y, A.z", "Accessors and init blocks");
        ExpectKotlin(check, "class A(val f: () -> Unit = {}) {\n    fun g() {}\n}", "A, A.g", "Lambda default in the primary constructor");
        ExpectKotlin(check, "class A : B by C() {\n    fun g() {}\n}", "A, A.g", "Interface delegation");
        ExpectKotlin(check, "class Box<T : Comparable<T>>(val t: T) where T : Any {\n    fun get(): T = t\n}", "Box, Box.get", "Generic bounds and where clause");
        ExpectKotlin(check, "class A {\n    companion object Factory {\n        fun create() = A()\n    }\n}", "A, A.Factory, A.Factory.create", "Named companion object");
        ExpectKotlin(check, "enum class E { A, B; fun f() = 1 }\nfun after() {}", "E, E.f, after", "Kotlin enum entries are not declarations");
        ExpectKotlin(check, "val a = 1; val b = 2\nfun c() {}; fun d() {}", "a, b, c, d", "Semicolon-separated declarations");
        ExpectKotlin(check, "class A {\n    @Test\n    fun `adds numbers`() {}\n    @get:Rule val rule = 1\n}", "A, A.adds numbers, A.rule", "Annotated members");
        ExpectKotlin(check, "typealias Handler = (String) -> Unit\nclass After", "After", "Type aliases");
        ExpectKotlin(check, "val open = 1\nval data = 2\nfun sealed() {}", "open, data, sealed", "Soft modifiers used as names");
        ExpectKotlin(check, "val (a, b) = pair\nfun f() {}", "f", "Destructuring declarations have no lens");
        ExpectKotlin(check, "val c = '\"'\nval d = '\\''\nclass After", "c, d, After", "Quote characters in char literals");
        ExpectKotlin(check, "val s = \"a${\"}\"}b\"\nclass After", "s, After", "Nested strings in templates");
        ExpectKotlin(check, "/* outer /* inner */ class Hidden */\nclass Visible", "Visible", "Nested block comments");
        ExpectKotlin(check, "class A {\n    fun f() {\n        if (x) {\n            fun nested() {}\n        }\n    }\n    fun g() {}\n}", "A, A.f, A.g", "Nested blocks inside a function body");
        ExpectKotlin(check, "abstract class A {\n    abstract fun f(): Int\n    abstract val p: Int\n    fun g() {}\n}", "A, A.f, A.p, A.g", "Abstract members without bodies");
        ExpectKotlin(check, "sealed class Result {\n    data class Ok(val v: Int) : Result()\n    object Fail : Result()\n}", "Result, Result.Ok, Result.Fail", "Nested sealed hierarchy");
        ExpectKotlin(check, "expect fun platform(): String\nactual class Impl", "platform, Impl", "expect/actual declarations");
        ExpectKotlin(check, "class A\r\n{\r\n    fun f() {}\r\n}\r\n", "A, A.f", "CRLF line endings and the brace on its own line");
        ExpectKotlin(check, "class A\nfun b() {\n    val x = 1\n}", "A, b", "A class without body before a function");

        var crlf = CodeDeclarationScanner.ScanKotlin("package p\r\n\r\nclass A {\r\n    fun f() {}\r\n}\r\n");
        var f = crlf.Single(x => x.Name == "f");
        check(f.NameLine == 3 && f.NameColumn == 8, "CRLF positions: " + f);

        var mains = CodeDeclarationScanner.ScanKotlin("fun String.main() {}\nclass C { fun main() {} }\nobject O { fun main() {} }\nfun main() {}");
        check(mains.Count(x => x.IsMain) == 1 && mains.Last().IsMain, "Only a top-level non-extension main is an entry point: " + string.Join(", ", mains.Where(x => x.IsMain)));
        var paramsMain = CodeDeclarationScanner.ScanKotlin("fun main(vararg args: String) {}");
        check(paramsMain.Single().IsMain, "vararg main is an entry point.");

        var open = CodeDeclarationScanner.ScanKotlin("open class A {\n    open fun f() {}\n    protected open val p = 1\n    override fun toString() = \"\"\n    final override fun hashCode() = 1\n}\ninterface I {\n    fun g()\n    val q: Int\n    private fun h() {}\n}");
        check(open.Where(x => x.CanBeImplemented).Select(x => x.QualifiedName).SequenceEqual(new[] { "A", "A.f", "A.p", "I", "I.g", "I.q" }),
            "Implementable Kotlin declarations: " + Names(open.Where(x => x.CanBeImplemented)));

        var tick = CodeDeclarationScanner.ScanKotlin("fun `a b`() {}").Single();
        check(tick.Name == "a b" && tick.NameLength == 5 && tick.EndOffset == 9, "Backticked name length includes the backticks: " + tick);

        var overloads = CodeDeclarationScanner.ScanKotlin("class A {\n    fun f() {}\n    fun f(x: Int) {}\n}");
        check(overloads.Count(x => x.QualifiedName == "A.f") == 2, "Overloads are separate declarations.");
        check(CodeDeclarationScanner.ScanKotlin("").Count == 0 && CodeDeclarationScanner.ScanKotlin("}}}{{{").Count == 0, "Empty and unbalanced input.");
        check(CodeDeclarationScanner.ScanKotlin("class").Count == 0 && CodeDeclarationScanner.ScanKotlin("fun").Count == 0 && CodeDeclarationScanner.ScanKotlin("@").Count == 0, "Truncated declarations.");
    }

    private static void RunJavaEdgeCases(Action<bool, string> check)
    {
        ExpectJava(check, "class A {\n    static { int x = 1; }\n    { int y = 2; }\n    void f() {}\n}", "A, A.f", "Static and instance initializers");
        ExpectJava(check, "class A {\n    void f() throws java.io.IOException, RuntimeException {\n        class Local {}\n    }\n    int g() { return 1; }\n}", "A, A.f, A.g", "throws clauses and local classes");
        ExpectJava(check, "class A {\n    int[] a = {1, 2};\n    Runnable r = () -> { int z = 0; };\n    void f() {}\n}", "A, A.a, A.r, A.f", "Array and lambda initializers");
        ExpectJava(check, "@interface Ann {\n    String value() default \"\";\n    int count();\n}", "Ann, Ann.value, Ann.count", "Annotation types");
        ExpectJava(check, "enum E { ; void f() {} }", "E, E.f", "Enum without constants");
        ExpectJava(check, "enum E {\n    @Deprecated A,\n    B,\n}", "E, E.A, E.B", "Enum constants with annotations and a trailing comma");
        ExpectJava(check, "record R(int x) {\n    R {\n        if (x < 0) throw new IllegalArgumentException();\n    }\n    static R of(int x) { return new R(x); }\n}", "R, R.of", "Records with compact constructors");
        ExpectJava(check, "sealed interface S permits A, B {}\nfinal class A implements S {}\nnon-sealed class B implements S {}", "S, A, B", "Sealed hierarchy");
        ExpectJava(check, "class Box<T extends Comparable<? super T>> {\n    Map<String, List<T>> map;\n    <R> R apply(java.util.function.Function<T, R> fn) { return null; }\n}", "Box, Box.map, Box.apply", "Nested generics");
        ExpectJava(check, "class A {\n    public A() {}\n    public A(int x) { this(); }\n}", "A, A.A, A.A", "Overloaded constructors");
        ExpectJava(check, "class A {\n    String s = \"class Fake { void f() {} }\";\n    char c = '\"';\n    void after() {}\n}", "A, A.s, A.c, A.after", "Strings and quote characters");
        ExpectJava(check, "class A {\n    @SuppressWarnings(\"unchecked\")\n    public void f(@Nonnull String s, int... rest) {}\n}", "A, A.f", "Annotated parameters and varargs");
        ExpectJava(check, "class A\r\n{\r\n    void f() {}\r\n}\r\n", "A, A.f", "CRLF and the brace on its own line");

        var mains = CodeDeclarationScanner.ScanJava("class A {\n    public static int main(String[] args) { return 0; }\n    static public void main(String... args) {}\n}\nclass B {\n    public void main(String[] args) {}\n}");
        check(mains.Count(x => x.IsMain) == 1 && mains.Single(x => x.IsMain).NameLine == 2, "Java main must be public static void: " + string.Join(", ", mains.Where(x => x.IsMain)));

        var abstracts = CodeDeclarationScanner.ScanJava("abstract class A {\n    abstract void f();\n    void g() {}\n    private interface Inner { void h(); }\n}");
        check(abstracts.Where(x => x.CanBeImplemented).Select(x => x.QualifiedName).SequenceEqual(new[] { "A", "A.f", "A.Inner", "A.Inner.h" }),
            "Implementable Java declarations: " + Names(abstracts.Where(x => x.CanBeImplemented)));
        var position = CodeDeclarationScanner.ScanJava("package p;\n\npublic class Main {\n    public static void main(String[] args) {}\n}").Single(x => x.IsMain);
        check(position.NameLine == 3 && position.NameColumn == 23, "Java name position: " + position);
    }
}
