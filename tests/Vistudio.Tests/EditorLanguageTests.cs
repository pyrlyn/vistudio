using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Editor;
using Vistudio.Core.Rust;
using Vistudio.Core.Structure;
using Vistudio.Core.Wasm;
using Vistudio.Core.Zig;

/// <summary>
/// The editor features shared by every <see cref="EditorLanguage"/> (feature-parity port): structure for Dart, Rust and
/// WebAssembly text, rainbow braces for Java, Dart, Zig and Rust, scroll bar map and inlay hint display settings for
/// Dart, Zig, Rust and WebAssembly, the Zig entry point for the Run | Debug lens, and the explicit stubs.
/// </summary>
internal static class EditorLanguageTests
{
    private const string Dart = @"import 'package:flutter/material.dart';

/// class InComment {
@immutable
abstract class Shape {
  double get area {
    return 0;
  }
}

class Counter extends StatefulWidget
    with Diagnosticable {
  const Counter({super.key});

  @override
  State<Counter> createState() {
    return _CounterState();
  }
}

mixin Logging on Object {
  void log(String message) {
    print('${message.length} {');
  }
}

extension on int {
  int twice() {
    return this * 2;
  }
}

Future<void> main() async {
  final label = switch (1) {
    1 => 'one',
    _ => 'other',
  };
  if (label.isEmpty) {
    print('}');
  } else {
    for (final c in label.split('')) {
      print(c);
    }
  }
  try {
    run(() {
      print('x');
    });
  } on FormatException catch (e) {
    print(e);
  }
  void helper(int x) {
    print(x);
  }
  final text = '''
  {
  ''';
}
";

    private const string Rust = @"//! Crate docs { not a block
use std::fmt;

#[derive(Debug, Clone)]
pub struct Point<'a> {
    name: &'a str,
    x: i32,
}

impl<'a> fmt::Display for Point<'a>
where
    'a: 'static,
{
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, ""{{{}}}"", self.x)
    }
}

pub(crate) trait Shape {
    fn area(&self) -> f64 {
        0.0
    }
}

extern ""C"" {
    fn abs(input: i32) -> i32;
}

/* nested /* comment { */ still comment } */
fn main() {
    let c = '{';
    let raw = r#""raw "" { string""#;
    let value = match c {
        '{' => {
            1
        }
        _ => 2,
        other => {
            3
        }
    };
    'outer: loop {
        while value > 0 {
            break 'outer;
        }
    }
    unsafe {
        abs(-1);
    }
    if value > 1 {
        println!(""{}"", raw);
    } else if value < 0 {
        println!(""neg"");
    }
    let add = |a: i32| {
        a + 1
    };
}

#[cfg(test)]
mod tests {
    #[test]
    fn adds() {
        assert_eq!(2, 2);
    }
}
";

    private const string Wat = @"(module
  ;; (func $commented
  (; block comment (func $hidden ;)
  (func $add (export ""add"") (param $a i32) (param $b i32) (result i32)
    local.get $a
    local.get $b
    i32.add)
  (func $count (param $n i32)
    block $exit
      loop $top
        local.get $n
        br_if $exit
        br $top
      end
    end
    (if (local.get $n)
      (then
        nop)
      (else
        nop)))
  (func $short (result i32) i32.const 1)
  (data (i32.const 0) "")"")
)
";

    internal static void Run(Action<bool, string> check)
    {
        Structure(check);
        Braces(check);
        Masks(check);
        Settings(check);
        ZigEntryPoint(check);
    }

    private static void Structure(Action<bool, string> check)
    {
        var dart = Describe(Dart, SourceStructure.Scan(EditorLanguage.Dart, Dart));
        Expect(check, dart, "Type abstract class Shape", "Dart class after an annotation");
        Expect(check, dart, "Member double get area", "Dart getter");
        Expect(check, dart, "Type class Counter extends StatefulWidget with Diagnosticable", "Dart multi-line class header");
        Expect(check, dart, "Member State<Counter> createState()", "Dart method after @override");
        Expect(check, dart, "Type mixin Logging on Object", "Dart mixin");
        Expect(check, dart, "Member void log(String message)", "Dart mixin method");
        Expect(check, dart, "Type extension on int", "Dart unnamed extension");
        Expect(check, dart, "Member Future<void> main() async", "Dart top-level function with async body");
        Expect(check, dart, "Conditional final label = switch (1)", "Dart switch expression");
        Expect(check, dart, "Conditional if (label.isEmpty)", "Dart if");
        Expect(check, dart, "Conditional else", "Dart else");
        Expect(check, dart, "Loop for (final c in label.split(''))", "Dart for-in");
        Expect(check, dart, "Statement try", "Dart try");
        Expect(check, dart, "Statement on FormatException catch (e)", "Dart on ... catch");
        Expect(check, dart, "Member void helper(int x)", "Dart local function");
        Expect(check, dart, "Expression ()", "Dart closure argument");
        check(!dart.Any(x => x.Contains("InComment")), "Dart comments have no blocks: " + string.Join(" | ", dart));
        check(dart.Count(x => x.StartsWith("Member Future<void> main")) == 1 && !dart.Any(x => x.Contains("text")), "Dart string braces (interpolation, quotes, triple quotes) are ignored");

        var rust = Describe(Rust, SourceStructure.Scan(EditorLanguage.Rust, Rust));
        Expect(check, rust, "Type pub struct Point<'a>", "Rust struct after an attribute, lifetime kept");
        Expect(check, rust, "Type impl<'a> fmt::Display for Point<'a> where 'a: 'static,", "Rust impl with a where clause on later lines");
        Expect(check, rust, "Member fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result", "Rust method");
        Expect(check, rust, "Type pub(crate) trait Shape", "Rust pub(crate) trait");
        Expect(check, rust, "Member fn area(&self) -> f64", "Rust default trait method");
        Expect(check, rust, "Type extern \"C\"", "Rust extern block keeps its ABI string in the header");
        Expect(check, rust, "Member fn main()", "Rust fn main after a nested block comment");
        Expect(check, rust, "Conditional let value = match c", "Rust match assigned with let");
        Expect(check, rust, "Conditional '{' =>", "Rust match arm with a block (char pattern)");
        Expect(check, rust, "Conditional other =>", "Rust match arm after a comma-terminated arm");
        Expect(check, rust, "Loop 'outer: loop", "Rust labeled loop");
        Expect(check, rust, "Loop while value > 0", "Rust while");
        Expect(check, rust, "Statement unsafe", "Rust unsafe block");
        Expect(check, rust, "Conditional if value > 1", "Rust if");
        Expect(check, rust, "Conditional else if value < 0", "Rust else if");
        Expect(check, rust, "Expression let add = |a: i32|", "Rust closure");
        Expect(check, rust, "Type mod tests", "Rust module after #[cfg(test)]");
        Expect(check, rust, "Member fn adds()", "Rust test function after #[test]");
        check(!rust.Any(x => x.Contains("Crate docs") || x.Contains("still comment") || x.Contains("_ => 2")), "Rust comments and one-line arms have no blocks: " + string.Join(" | ", rust));

        var wat = Describe(Wat, SourceStructure.Scan(EditorLanguage.Wat, Wat));
        Expect(check, wat, "Type (module", "WAT module");
        Expect(check, wat, "Member (func $add (export \"add\") (param $a i32) (param $b i32) (result i32)", "WAT function with its signature line");
        Expect(check, wat, "Member (func $count (param $n i32)", "WAT function");
        Expect(check, wat, "Statement block $exit", "WAT flat block ... end");
        Expect(check, wat, "Loop loop $top", "WAT flat loop ... end");
        Expect(check, wat, "Conditional (if (local.get $n)", "WAT folded if");
        Expect(check, wat, "Conditional (then", "WAT folded then");
        Expect(check, wat, "Conditional (else", "WAT folded else");
        check(!wat.Any(x => x.Contains("commented") || x.Contains("hidden") || x.Contains("$short") || x.Contains("(data")), "WAT comments, strings and one-line forms have no blocks: " + string.Join(" | ", wat));
        var module = SourceStructure.Scan(EditorLanguage.Wat, Wat)[0];
        check(Wat[module.OpenBrace] == '(' && Wat[module.CloseBrace] == ')' && module.CloseBrace == Wat.LastIndexOf(')'), "WAT folded blocks span their parentheses");
        var flat = SourceStructure.Scan(EditorLanguage.Wat, Wat).First(b => b.Kind == CodeBlockKind.Loop);
        check(Wat.Substring(flat.CloseBrace - 2, 3) == "end", "WAT flat blocks close at their end");
        check(WatStructureScanner.Scan("(module\n  (func\n").Count == 0 && WatStructureScanner.Scan(")) end\n(").Count == 0, "Unclosed and unmatched WAT forms are tolerated");

        const string ZigText = "pub fn main() void {\n    while (true) {\n        break;\n    }\n}\n";
        check(SourceStructure.Scan(EditorLanguage.Zig, ZigText).Count == 2, "Zig structure goes through the shared dispatcher");
        check(SourceStructure.Scan(EditorLanguage.Kotlin, "fun f() {\n}\n").Count == 1 && SourceStructure.Scan(EditorLanguage.Java, "class A {\n}\n").Count == 1, "Kotlin and Java structure is unchanged behind the dispatcher");
        check(SourceStructure.Scan(EditorLanguage.Rust, "fn f() {\n    if x {\n}\n").Count == 1, "Unclosed Rust blocks are dropped, closed ones stay");
        check(SourceStructure.Scan(EditorLanguage.Dart, "}\n}\n{").Count == 0, "Unmatched Dart braces are tolerated");
    }

    private static void Braces(Action<bool, string> check)
    {
        string Colored(EditorLanguage language, string text) =>
            new string(RainbowBraces.Scan(language, text).Select(b => text[b.Position]).ToArray());

        check(Colored(EditorLanguage.Java, "class A { String s = \"${x} {\"; char c = '}'; /* { */ void f() { } }") == "{{}}", "Java: string, char and comment braces are not colored, and \"${\" is text in Java");
        check(Colored(EditorLanguage.Kotlin, "val s = \"${x}\"") == "{}", "Kotlin still colors template braces");
        check(Colored(EditorLanguage.Dart, "void f() { print('${a} {'); /* { /* } */ */ }") == "{}", "Dart: strings with interpolation and nested comments are skipped");
        check(Colored(EditorLanguage.Zig, "const s = \"{\"; // {\nfn f() void { const c = '}';\n  \\\\ multiline {\n}") == "{}", "Zig: strings, characters, comments and \\\\ lines are skipped");
        check(Colored(EditorLanguage.Rust, "fn f<'a>(x: &'a str) { let c = '{'; let s = r#\"}\"#; /* /* { */ } */ }") == "{}", "Rust: lifetimes, chars, raw strings and nested comments are handled");
        check(RainbowBraces.Scan(EditorLanguage.Wat, "(module (func))").Count == 0 && !RainbowBraces.Supports(EditorLanguage.Wat), "WAT rainbow braces are an explicit stub");

        var nested = RainbowBraces.Scan(EditorLanguage.Rust, "{ { } { } }");
        check(nested[0].Color == nested[5].Color && nested[1].Color == nested[2].Color && nested[3].Color == nested[4].Color, "Pairs share a color");
        check(nested[1].Color != nested[3].Color && nested[0].Color != nested[1].Color && nested[0].Color != nested[3].Color, "Siblings and nesting levels differ");
        var kotlin = "fun f() { if (x) { } }";
        check(KotlinBraceScanner.Scan(kotlin).Select(b => b.Color).SequenceEqual(RainbowBraces.Scan(EditorLanguage.Java, kotlin).Select(b => b.Color)), "The shared palette assigns the colors the Kotlin scanner assigned");
        check(RainbowBraces.FromMasked("} { ").Count == 1, "An unmatched closing brace is left uncolored");
        check(RainbowBraces.Scan(EditorLanguage.Rust, new string('{', 20000) + new string('}', 20000)).Count == 40000, "Deep nesting uses no recursion");
    }

    private static void Masks(Action<bool, string> check)
    {
        void Same(string input, string expected, string what)
        {
            var masked = RustSourceScanner.Mask(input);
            check(masked == expected && masked.Length == input.Length, what + ": `" + masked + "`");
        }
        Same("a // {\nb", "a     \nb", "Line comment blanked, line break kept");
        Same("/* a /* b */ c */x", "                 x", "Nested block comments");
        Same("\"a{\\\"b\"", "0      ", "String with an escaped quote");
        Same("r#\"a\"b\"#;", "0       ;", "Raw string with hashes");
        Same("br\"x\" b\"y\" c\"z\" b'q'", "0     0    0    0   ", "Byte, C and byte character literals");
        Same("'a' '\\n' '\\u{1F600}'", "0   0    0          ", "Character literals with escapes");
        Same("&'a str 'outer: loop", "& a str  outer: loop", "Lifetimes and labels keep their names");
        Same("r#type bar\"", "r#type bar0", "Raw identifiers are not strings");
        Same("\"line\r\nnext\"", "0    \r\n     ", "Multi-line strings keep their line breaks");
        check(RustSourceScanner.Mask("'😀'") == "0   ", "A character outside the BMP is one character literal");
        check(DartSourceScanner.Mask("'{'").Length == 3, "Dart mask keeps offsets");
    }

    private static void Settings(Action<bool, string> check)
    {
        IEditorSettings[] all = { new KotlinSettings(), new DartSettings(), new ZigSettings(), new RustSettings(), new WasmSettings() };
        check(all.All(s => s.ScrollBarMode == ScrollBarMode.Inherit && s.ScrollBarMapWidth == ScrollBarMapWidth.Medium && s.ScrollBarMapPreview && s.InlayHintsDisplay == InlayHintsDisplay.Always),
            "Every language starts with Kotlin's editor defaults (inherit the scroll bar, always show inlay hints)");
        check(all.Take(4).All(s => s.RainbowBraces) && !((IEditorSettings)new WasmSettings()).RainbowBraces, "Rainbow braces default on, except the WebAssembly stub");
        check(((IEditorSettings)new KotlinSettings { InlayHints = InlayHintsDisplay.Never }).InlayHintsDisplay == InlayHintsDisplay.Never, "Kotlin keeps its stored InlayHints name behind the interface");
        var rustMap = ScrollBarMap.Resolve(new RustSettings { ScrollBarMode = ScrollBarMode.Map, ScrollBarMapWidth = ScrollBarMapWidth.Narrow });
        check(Equals(rustMap[ScrollBarMap.MapModeOption], true) && Equals(rustMap[ScrollBarMap.WidthOption], 33.0), "Scroll bar map options resolve for a non-JVM language");
        check(InlayHints.EnableKind(new ZigSettings { InlayHintsDisplay = InlayHintsDisplay.Never }.InlayHintsDisplay) == InlayHints.EnableKind(InlayHintsDisplay.Never), "Inlay hint display maps through the same Visual Studio enum value");

        // Stored and overridden like every other setting of the language.
        var serializer = new XmlSerializer(typeof(DartSettings));
        var writer = new StringWriter();
        serializer.Serialize(writer, new DartSettings { ScrollBarMode = ScrollBarMode.Bar, RainbowBraces = false, InlayHintsDisplay = InlayHintsDisplay.WhileHoldingAltF1 });
        var restored = (DartSettings)serializer.Deserialize(new StringReader(writer.ToString()));
        check(restored.ScrollBarMode == ScrollBarMode.Bar && !restored.RainbowBraces && restored.InlayHintsDisplay == InlayHintsDisplay.WhileHoldingAltF1, "Editor settings survive the settings XML round trip");
        var wasmXml = new StringWriter();
        new XmlSerializer(typeof(WasmSettings)).Serialize(wasmXml, new WasmSettings());
        check(!wasmXml.ToString().Contains("RainbowBraces"), "The WebAssembly brace stub is not stored");

        var root = Path.Combine(Path.GetTempPath(), "Vistudio editor settings " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".vs"));
        try
        {
            File.WriteAllText(WorkspaceSettings.FilePath(root), "{ \"vistudio.rust.scrollBarMode\": \"map\", \"vistudio.zig.rainbowBraces\": false, \"vistudio.dart.inlayHintsDisplay\": \"Never\", \"vistudio.wasm.rainbowBraces\": true }");
            var rust = new RustSettings();
            var zig = new ZigSettings();
            var dart = new DartSettings();
            var wasm = new WasmSettings();
            check(WorkspaceSettings.ApplyTo(rust, root).Count == 0 && rust.ScrollBarMode == ScrollBarMode.Map, "vistudio.rust.scrollBarMode overrides per window");
            WorkspaceSettings.ApplyTo(zig, root);
            WorkspaceSettings.ApplyTo(dart, root);
            check(!zig.RainbowBraces && dart.InlayHintsDisplay == InlayHintsDisplay.Never, "vistudio.zig.rainbowBraces and vistudio.dart.inlayHintsDisplay override per window");
            var wasmWarnings = WorkspaceSettings.ApplyTo(wasm, root);
            check(wasmWarnings.Count == 1 && wasmWarnings[0].Contains("rainbowBraces"), "vistudio.wasm.rainbowBraces is reported as unknown: " + string.Join("; ", wasmWarnings));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ZigEntryPoint(Action<bool, string> check)
    {
        var functions = ZigSourceScanner.Functions("const S = struct {\n    fn main() void {}\n};\npub fn main() !void {\n}\n");
        check(functions.Count(f => f.QualifiedName == "main") == 1 && functions.Any(f => f.QualifiedName == "S.main"),
            "Only the top-level main is the entry point the Zig Run | Debug lens is placed on (S.main is a method)");
    }

    private static string[] Describe(string text, System.Collections.Generic.IReadOnlyList<CodeBlock> blocks) =>
        blocks.Select(b => b.Kind + " " + Regex.Replace(text.Substring(b.HeaderStart, b.HeaderEnd - b.HeaderStart), @"\s+", " ")).ToArray();

    private static void Expect(Action<bool, string> check, string[] blocks, string expected, string what) =>
        check(blocks.Contains(expected), what + ": expected `" + expected + "` in " + string.Join(" | ", blocks));
}
