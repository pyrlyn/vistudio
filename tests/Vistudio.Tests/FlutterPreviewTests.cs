using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Dart;
using Vistudio.Core.Dart.Preview;

/// <summary>
/// Flutter widget preview without an SDK: Flutter project detection, Flutter SDK search (setting, FLUTTER_ROOT, PATH,
/// fvm, mise, asdf, standard folders), version support, @Preview and widget discovery (own scanner and analysis server
/// answers), configurations, the quick fix, the temporary host project, previewer events and the LSP/DTD framing.
/// </summary>
internal static class FlutterPreviewTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Pubspecs(check);
        Workspace(Path.Combine(root, "flutter workspace"), check);
        SdkSearch(Path.Combine(root, "flutter sdk search"), check);
        Versions(check);
        Scanner(check);
        ServerAnswers(check);
        Configurations(check);
        QuickFix(check);
        HostProject(Path.Combine(root, "flutter host"), check);
        Events(check);
        Framing(check).GetAwaiter().GetResult();
        Settings(check);
    }

    private static void Pubspecs(Action<bool, string> check)
    {
        const string app = "name: counter_app\nenvironment:\n  sdk: ^3.9.0\ndependencies:\n  flutter:\n    sdk: flutter\n  cupertino_icons: ^1.0.8\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\n";
        check(FlutterPubspec.HasFlutterDependency(app) && FlutterPubspec.PackageName(app) == "counter_app", "Flutter app: dependencies.flutter.sdk = flutter");
        check(!FlutterPubspec.HasFlutterDependency("name: tool\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\n"), "Only flutter_test in dev_dependencies is not a Flutter dependency");
        check(!FlutterPubspec.HasFlutterDependency("name: tool\ndependencies:\n  path: ^1.9.0\n# flutter:\n#   sdk: flutter\n"), "Commented flutter dependency does not count");
        check(FlutterPubspec.HasFlutterDependency("name: x\ndependencies:\n  flutter: {sdk: flutter}\n"), "Flow mapping flutter: {sdk: flutter}");
        check(FlutterPubspec.HasFlutterDependency("name: 'x'\r\ndependencies:\r\n    meta: any\r\n    flutter:   # the SDK\r\n        sdk: \"flutter\"\r\n"), "CRLF, wide indentation, quotes and comments");
        check(!FlutterPubspec.HasFlutterDependency("name: x\ndependencies:\n  flutter_bloc: ^9.0.0\n  provider:\n    sdk: flutter\n"), "Only the 'flutter' key counts");
        check(!FlutterPubspec.HasFlutterDependency("name: x\ndependencies:\n  flutter: ^1.0.0\n"), "A hosted package named flutter is not the SDK");
        check(!FlutterPubspec.HasFlutterDependency("name: x\ndependencies:\n  foo:\n    path: ../foo\nflutter:\n  sdk: flutter\n"), "A top-level flutter: section is not a dependency");
        check(FlutterPubspec.PackageName("description: x\nname: \"quoted_name\" # c\n") == "quoted_name", "Quoted package name");
    }

    private static void Workspace(string root, Action<bool, string> check)
    {
        Write(Path.Combine(root, "apps", "shop", "pubspec.yaml"), "name: shop\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Write(Path.Combine(root, "packages", "ui_kit", "pubspec.yaml"), "name: ui_kit\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Write(Path.Combine(root, "packages", "core", "pubspec.yaml"), "name: core\ndependencies:\n  meta: any\n");
        Write(Path.Combine(root, "apps", "shop", "build", "nested", "pubspec.yaml"), "name: ignored_build\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Write(Path.Combine(root, "apps", "shop", ".dart_tool", "x", "pubspec.yaml"), "name: ignored_tool\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Write(Path.Combine(root, "apps", "shop", ".widget_preview", "pubspec.yaml"), "name: widget_preview_scaffold\ndependencies:\n  flutter:\n    sdk: flutter\n");
        Write(Path.Combine(root, "apps", "shop", "lib", "main.dart"), "void main() {}\n");
        var found = FlutterWorkspace.FindProjects(new[] { root, Path.Combine(root, "apps", "shop") });
        check(found.Select(p => p.Name).SequenceEqual(new[] { "shop", "ui_kit" }), "Workspace scan finds the Flutter packages once, skipping Dart-only, build, .dart_tool and .widget_preview: " + string.Join(", ", found));
        check(FlutterWorkspace.ProjectOf(Path.Combine(root, "apps", "shop", "lib", "main.dart"))?.Name == "shop", "Flutter package of a file");
        check(FlutterWorkspace.ProjectOf(Path.Combine(root, "packages", "core", "pubspec.yaml")) == null, "A Dart-only package is not a Flutter project");
        check(FlutterWorkspace.FindProjects(new[] { Path.Combine(root, "missing") }).Count == 0, "Missing roots are ignored");
        check(FlutterWorkspace.FindProjects(new[] { root }, maxDepth: 1).Count == 0, "Depth limit");
    }

    private static void SdkSearch(string root, Action<bool, string> check)
    {
        string Sdk(string folder, string version = "3.50.0")
        {
            Write(Path.Combine(folder, "bin", "flutter.bat"), "@echo off\n");
            Write(Path.Combine(folder, "bin", "cache", "dart-sdk", "bin", "dart.exe"), "");
            Write(Path.Combine(folder, "bin", "cache", "flutter.version.json"), "{\"frameworkVersion\":\"" + version + "\"}");
            return Path.GetFullPath(folder);
        }
        var home = Path.Combine(root, "home");
        var local = Path.Combine(root, "local");
        var project = Path.Combine(root, "work", "app");
        Directory.CreateDirectory(project);
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var env = new FlutterSdkEnvironment(name => variables.TryGetValue(name, out var v) ? v : null, home, local, Path.Combine(root, "pf"), Path.Combine(root, "drive"));

        check(FlutterSdkLocator.Locate("", env, project).Location == null, "No SDK anywhere: not found");
        check(FlutterSdkLocator.Locate("", env, project).Error!.Contains("FLUTTER_ROOT (not set)"), "The error lists what was searched");

        var standard = Sdk(Path.Combine(home, "development", "flutter"));
        var found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == standard && found.Source == FlutterSdkSource.StandardFolder, "Standard folder ~\\development\\flutter");

        var asdf = Sdk(Path.Combine(home, ".asdf", "installs", "flutter", "3.38.1-stable"), "3.38.1");
        Sdk(Path.Combine(home, ".asdf", "installs", "flutter", "3.36.0-stable"), "3.36.0");
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == asdf && found.Source == FlutterSdkSource.Asdf, "asdf: newest installed version wins over standard folders: " + found);

        var misePinned = Sdk(Path.Combine(local, "mise", "installs", "flutter", "3.41.0-stable"), "3.41.0");
        var miseNewest = Sdk(Path.Combine(local, "mise", "installs", "flutter", "3.50.0-stable"));
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == miseNewest && found.Source == FlutterSdkSource.Mise, "mise without a pin: newest installed version");
        Write(Path.Combine(root, "work", "mise.toml"), "[tools]\nflutter = \"3.41.0\"\n");
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == misePinned && found.Detail.Contains("project pin 3.41.0"), "mise: the project's pin (parent folder mise.toml) wins: " + found);
        check(FlutterSdkLocator.ToolManagerPinnedVersion(Path.Combine(root, "work"), FlutterSdkSource.Mise) == "3.41.0", "mise.toml pin parsed");
        Write(Path.Combine(root, "tv", ".tool-versions"), "nodejs 22.0.0\nflutter 3.38.1-stable\n");
        check(FlutterSdkLocator.ToolManagerPinnedVersion(Path.Combine(root, "tv"), FlutterSdkSource.Asdf) == "3.38.1-stable", ".tool-versions pin parsed");

        var fvmPinned = Sdk(Path.Combine(local, "fvm", "versions", "3.44.0"), "3.44.0");
        Write(Path.Combine(project, ".fvmrc"), "{\"flutter\": \"3.44.0\"}");
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == fvmPinned && found.Source == FlutterSdkSource.Fvm, "fvm: .fvmrc pin in the fvm cache wins over mise and asdf: " + found);
        Write(Path.Combine(root, "legacy", ".fvm", "fvm_config.json"), "{\"flutterSdkVersion\": \"3.22.0\"}");
        check(FlutterSdkLocator.FvmPinnedVersion(Path.Combine(root, "legacy")) == "3.22.0", "fvm 2 fvm_config.json pin parsed");
        Write(Path.Combine(root, "evil", ".fvmrc"), "{\"flutter\": \"..\\\\..\\\\x\"}");
        check(FlutterSdkLocator.FvmPinnedVersion(Path.Combine(root, "evil")) == null, "fvm pin with path separators is ignored");

        var onPath = Sdk(Path.Combine(root, "path sdk"));
        variables["PATH"] = Path.Combine(root, "nothing") + ";" + Path.Combine(onPath, "bin");
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == onPath && found.Source == FlutterSdkSource.Path, "PATH wins over version managers");

        var fromVariable = Sdk(Path.Combine(root, "flutter root"));
        variables["FLUTTER_ROOT"] = fromVariable;
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == fromVariable && found.Source == FlutterSdkSource.FlutterRootVariable, "FLUTTER_ROOT wins over PATH");

        var configured = Sdk(Path.Combine(root, "configured"));
        found = FlutterSdkLocator.Locate(Path.Combine(configured, "bin"), env, project).Location;
        check(found?.Root == configured && found.Source == FlutterSdkSource.Setting, "The setting wins (its bin folder is accepted)");
        var invalid = FlutterSdkLocator.Locate(Path.Combine(root, "nope"), env, project);
        check(invalid.Location == null && invalid.Error!.Contains("configured Flutter SDK folder"), "An invalid setting is an error, not replaced by FLUTTER_ROOT");

        variables["FLUTTER_ROOT"] = Path.Combine(root, "stale");
        found = FlutterSdkLocator.Locate(null, env, project).Location;
        check(found?.Root == onPath, "A stale FLUTTER_ROOT falls through to PATH");

        var sdk = DartToolchain.Find(new DartSettings(), env, project);
        check(sdk?.FlutterRoot == onPath && sdk.Dart == Path.Combine(onPath, "bin", "cache", "dart-sdk", "bin", "dart.exe"), "DartToolchain uses the same search (Flutter's bundled dart.exe)");
        check(DartToolchain.Find(new DartSettings(), FlutterSdkEnvironment.PathOnly(""), project) == null, "PATH-only search with an empty PATH finds nothing");
        check(FlutterSdkLocator.StandardFolders(env).Any(f => f.EndsWith(Path.Combine("scoop", "apps", "flutter", "current"))), "Scoop location is a standard folder");
    }

    private static void Versions(Action<bool, string> check)
    {
        check(FlutterVersion.TryParse("3.50.0")!.CompareTo(FlutterVersion.TryParse("3.35.0")) > 0, "3.50.0 > 3.35.0");
        check(FlutterVersion.TryParse("3.35.0-0.1.pre")!.CompareTo(FlutterVersion.TryParse("3.35.0")) < 0, "Pre-release sorts before its release");
        check(FlutterVersion.TryParse("3.38.1-stable")!.CompareTo(FlutterVersion.TryParse("3.38.1")) == 0, "Channel suffix is not a pre-release");
        check(FlutterVersion.TryParse("v3.41")?.ToString() == "3.41.0" && FlutterVersion.TryParse("abc") == null, "Version parsing: v prefix, missing patch, garbage");
        check(WidgetPreviewSupport.Check("3.50.0") == null && WidgetPreviewSupport.Check("3.35.0-0.2.pre") == null, "3.50 and 3.35 pre-releases support the previewer");
        var old = WidgetPreviewSupport.Check("3.32.8");
        check(old != null && old.Contains("flutter upgrade") && old.Contains("3.35"), "3.32 is rejected with an upgrade hint: " + old);
        check(WidgetPreviewSupport.Check(null)!.Contains("could not be read"), "Unknown version: explanation");
    }

    private const string PreviewSource = @"import 'package:flutter/material.dart';
import 'package:flutter/widget_previews.dart';

// @Preview(name: 'commented out')
/// Docs mention @Preview(name: 'in a doc comment') too.
class GreetingCard extends StatelessWidget {
  const GreetingCard({super.key, this.name = 'x'});

  @Preview(name: 'Named constructor', group: 'Cards')
  const GreetingCard.preview({super.key}) : name = 'preview';

  @Preview(name: 'Factory')
  factory GreetingCard.factoryPreview() => const GreetingCard.preview();

  @Preview(size: Size.square(200))
  static Widget staticPreview() => const Text('static');

  @Preview()
  Widget instancePreview() => const Text('bad');

  final String name;

  @override
  Widget build(BuildContext context) => Text('Hello $name, see @Preview(name: \'string\')');
}

@Preview(name: 'Top level', size: Size(320, 120), textScaleFactor: 1.3, brightness: Brightness.dark, theme: myTheme)
@Deprecated('old')
Widget greetingPreview() => const GreetingCard();

@Preview(name: 'Builder')
WidgetBuilder builderPreview() => (context) => const GreetingCard();

@Preview()
Widget _privatePreview() => const GreetingCard();

@Preview()
Widget withArgs(String title) => Text(title);

class BrightnessPreview extends MultiPreview {
  const BrightnessPreview();
  @override
  List<Preview> get previews => const [Preview(brightness: Brightness.light), Preview(brightness: Brightness.dark)];
}

@BrightnessPreview()
Widget multi() => const GreetingCard();

class Counter extends StatefulWidget {
  const Counter({super.key});
  @override
  State<Counter> createState() => _CounterState();
}

class _CounterState extends State<Counter> {
  @override
  Widget build(BuildContext context) => const Text('0');
}

abstract class BaseTile extends StatelessWidget {
  const BaseTile({super.key});
}

class FancyTile extends BaseTile {
  FancyTile({super.key, required this.title});
  FancyTile.sample({super.key}) : title = 'sample';
  final String title;
  @override
  Widget build(BuildContext context) => Text(title);
}

class NeedsArgs extends StatelessWidget {
  const NeedsArgs(this.value, {super.key});
  final int value;
  @override
  Widget build(BuildContext context) => Text('$value');
}

class Implicit extends StatelessWidget {
  @override
  Widget build(BuildContext context) => const Text('implicit');
}

class _Hidden extends StatelessWidget {
  const _Hidden();
  @override
  Widget build(BuildContext context) => const SizedBox();
}

class Riverpod extends ConsumerWidget {
  const Riverpod({super.key});
}

class Generic<T extends List<int>> extends StatelessWidget {
  const Generic({super.key});
  @override
  Widget build(BuildContext context) => const SizedBox();
}

class FromPackage extends SomePackageWidget {
  const FromPackage({super.key});
}

class Model extends ChangeNotifier {}
";

    private static void Scanner(Action<bool, string> check)
    {
        var file = WidgetPreviewScanner.Scan(PreviewSource);
        check(file.HasPreviewImport && file.PreviewPrefix == null, "widget_previews import found");
        var names = file.Targets.Select(t => t.Name).ToList();
        check(names.SequenceEqual(new[] { "GreetingCard.preview", "GreetingCard.factoryPreview", "GreetingCard.staticPreview", "GreetingCard.instancePreview", "greetingPreview", "builderPreview", "_privatePreview", "withArgs", "multi" }),
            "@Preview targets (comments, doc comments and strings ignored; multi-preview subclass recognized): " + string.Join(", ", names));
        WidgetPreviewTarget T(string name) => file.Targets.First(t => t.Name == name);
        check(T("GreetingCard.preview").Kind == PreviewTargetKind.Constructor && T("GreetingCard.preview").Annotation.Group == "Cards" && T("GreetingCard.preview").Problem == null, "Named constructor preview with group");
        check(T("GreetingCard.factoryPreview").Kind == PreviewTargetKind.Factory, "Factory preview");
        check(T("GreetingCard.staticPreview").Kind == PreviewTargetKind.StaticMethod && T("GreetingCard.staticPreview").Annotation.Width == 200 && T("GreetingCard.staticPreview").Annotation.Height == 200, "Static method preview with Size.square");
        check(T("GreetingCard.instancePreview").Problem!.Contains("static"), "Instance method preview is reported");
        var top = T("greetingPreview");
        check(top.Kind == PreviewTargetKind.Function && top.Annotation.Name == "Top level" && top.Annotation.Width == 320 && top.Annotation.Height == 120
              && top.Annotation.TextScaleFactor == 1.3 && top.Annotation.Brightness == "dark" && top.Annotation.HasTheme && !top.Annotation.HasLocalizations,
            "Top-level preview arguments: name, size, textScaleFactor, brightness, theme: " + top.Annotation.Describe());
        check(PreviewSource.Split('\n')[top.Line].StartsWith("@Preview(name: 'Top level'"), "Target line is the annotation line");
        check(PreviewSource.Substring(top.NameStart, top.NameEnd - top.NameStart) == "greetingPreview", "Target name offsets (another annotation in between)");
        check(T("_privatePreview").Problem!.Contains("private") && T("withArgs").Problem!.Contains("required"), "Private and parameterized previews are reported");
        check(T("multi").Annotation.IsMultiPreview && T("builderPreview").Problem == null, "MultiPreview subclass and WidgetBuilder previews");

        var widgets = file.Widgets.ToDictionary(w => w.Name);
        check(widgets["GreetingCard"].IsWidget == true && widgets["GreetingCard"].HasPreview && widgets["GreetingCard"].Construction == "const GreetingCard()", "Stateless widget with previews and a const constructor");
        check(widgets["Counter"].IsWidget == true && widgets["Counter"].Construction == "const Counter()" && !widgets["Counter"].HasPreview, "Stateful widget without @Preview");
        check(!widgets.ContainsKey("_CounterState") && !widgets.ContainsKey("Model") && !widgets.ContainsKey("BrightnessPreview"), "State, ChangeNotifier and MultiPreview classes are not widgets");
        check(widgets["BaseTile"].IsAbstract && widgets["BaseTile"].Problem!.Contains("abstract"), "Abstract widget base is reported");
        check(widgets["FancyTile"].IsWidget == true && widgets["FancyTile"].Construction == "FancyTile.sample()", "Indirect subclass through a class of the same file; named constructor without required arguments");
        check(widgets["NeedsArgs"].Construction == null && widgets["NeedsArgs"].Problem!.Contains("needs arguments"), "Positional constructor parameter: no automatic preview");
        check(widgets["Implicit"].Construction == "Implicit()", "Implicit default constructor");
        check(widgets["_Hidden"].Problem!.Contains("private"), "Private widget is reported");
        check(widgets["Riverpod"].IsWidget == true, "Well-known package base (ConsumerWidget) counts as a widget");
        check(widgets["Generic"].IsWidget == true && widgets["Generic"].SuperClass == "StatelessWidget", "Generic class with nested type arguments");
        check(widgets["FromPackage"].IsWidget == null, "Unknown base class: left to the analysis server");

        int At(string marker) => PreviewSource.IndexOf(marker, StringComparison.Ordinal);
        check(PreviewChoice.For(file, At("class Counter")).WrapWidget == "Counter", "Preview Widget with the caret on a widget without @Preview wraps it");
        check(PreviewChoice.For(file, At("Widget build(BuildContext context) => Text('Hello")) is var inCard && inCard.HasPreview && inCard.WrapWidget == null, "Caret inside a class with previews: the file's previews");
        check(PreviewChoice.For(file, 0).HasPreview && PreviewChoice.For(file, 0).WrapWidget == null, "Caret outside classes in a file with previews: the file's previews");
        var plain = WidgetPreviewScanner.Scan("class Solo extends StatelessWidget { const Solo(); }\nclass Model {}\n");
        check(PreviewChoice.For(plain, plain.Widgets[0].BodyClose + 5).WrapWidget == "Solo", "A file with one previewable widget and no previews wraps it");
        var two = WidgetPreviewScanner.Scan("class A extends StatelessWidget { const A(); }\nclass B extends StatelessWidget { const B(); }\n");
        check(PreviewChoice.For(two, two.Widgets[1].BodyClose + 1).Message!.Contains("A, B"), "Several candidates and no previews: the message names them");
        var needs = WidgetPreviewScanner.Scan("class N extends StatelessWidget { const N(this.v); final int v; }");
        check(PreviewChoice.For(needs, 3).Message!.Contains("needs arguments"), "A widget that needs arguments: explained");

        var prefixed = WidgetPreviewScanner.Scan("import 'package:flutter/widget_previews.dart' as wp;\nimport 'package:flutter/widgets.dart' as w;\n\n@wp.Preview(name: 'p')\nw.Widget p() => const w.Text('x');\n\nclass A extends w.StatelessWidget { const A(); }\n");
        check(prefixed.PreviewPrefix == "wp" && prefixed.Targets.Single().Name == "p" && prefixed.Widgets.Single().IsWidget == true, "Prefixed imports: @wp.Preview and w.StatelessWidget");
        var noImport = WidgetPreviewScanner.Scan("// import 'package:flutter/widget_previews.dart';\nclass A extends StatelessWidget { const A(); }\n");
        check(!noImport.HasPreviewImport && noImport.Targets.Count == 0, "A commented import does not count");
        check(WidgetPreviewScanner.HasRequiredParameters(" {super.key, this.x = 1} ") == false && WidgetPreviewScanner.HasRequiredParameters("this.a, [this.b]") && WidgetPreviewScanner.HasRequiredParameters("{required this.a}")
              && !WidgetPreviewScanner.HasRequiredParameters("[int a = 0]"), "Required parameter detection");
    }

    private static void ServerAnswers(Action<bool, string> check)
    {
        var answer = JObject.Parse(@"{""namespaces"":{},""scriptUris"":[""file:///C:/app/lib/a.dart""],""previews"":[
            {""functionName"":""greetingPreview"",""hasError"":false,""dependencyHasErrors"":false,""isBuilder"":false,""isMultiPreview"":false,""packageName"":""app"",
             ""position"":{""line"":28,""character"":7},""previewAnnotation"":""Preview(name: 'Top level')"",""scriptUri"":""file:///C:/app/lib/a.dart"",""libraryUri"":""package:app/a.dart""},
            {""functionName"":""fromOtherPackageAnnotation"",""hasError"":true,""dependencyHasErrors"":false,""isBuilder"":true,""isMultiPreview"":true,
             ""position"":{""line"":2,""character"":7},""previewAnnotation"":""ThemePreviews()"",""scriptUri"":""file:///C:/app/lib/a.dart"",""libraryUri"":""package:app/a.dart""}]}");
        var previews = WidgetPreviewLsp.ParsePreviews(answer);
        check(previews.Count == 2 && previews[0].FunctionName == "greetingPreview" && previews[0].Line == 28 && previews[1].IsMultiPreview && previews[1].HasError && previews[1].IsBuilder,
            "Analysis server previews parsed");
        check(WidgetPreviewLsp.ParsePreviews(new JObject { ["type"] = "Map", ["result"] = answer }).Count == 2, "DTD-wrapped answer parsed");
        check(WidgetPreviewLsp.ParsePreviews(JValue.CreateNull()).Count == 0 && WidgetPreviewLsp.ParsePreviews(new JArray()).Count == 0, "Unknown shapes give no previews");

        var text = "import 'package:flutter/widget_previews.dart';\nimport 'x.dart';\n\n@ThemePreviews()\nWidget fromOtherPackageAnnotation() => const X();\n";
        var merged = WidgetPreviewLsp.Merge(WidgetPreviewScanner.Scan(text).Targets, new[]
        {
            new LspWidgetPreview("fromOtherPackageAnnotation", 4, 7, null, "ThemePreviews()", false, false, false, true),
        }, text);
        check(merged.Count == 1 && merged[0].Annotation.IsMultiPreview && merged[0].Line == 4, "A preview only the analysis server knows (imported MultiPreview) is added");

        var symbols = JArray.Parse(@"[{""name"":""GreetingCard"",""kind"":5,""range"":{""start"":{""line"":5,""character"":0},""end"":{""line"":20,""character"":1}},
            ""selectionRange"":{""start"":{""line"":5,""character"":6},""end"":{""line"":5,""character"":18}},""children"":[{""name"":""build"",""kind"":6,""range"":{}}]},
            {""name"":""main"",""kind"":12,""range"":{""start"":{""line"":1,""character"":0}}}]");
        var classes = WidgetPreviewLsp.ParseClasses(symbols);
        check(classes.Count == 1 && classes[0].Name == "GreetingCard" && classes[0].Line == 5 && classes[0].Character == 6, "Class symbols from documentSymbol (selection range)");
        check(WidgetPreviewLsp.IsFrameworkWidget(JObject.Parse("{\"name\":\"StatelessWidget\",\"uri\":\"file:///C:/flutter/packages/flutter/lib/src/widgets/framework.dart\"}"))
              && !WidgetPreviewLsp.IsFrameworkWidget(JObject.Parse("{\"name\":\"StatelessWidget\",\"uri\":\"file:///C:/app/lib/fake.dart\"}")), "Type hierarchy: only Flutter's StatelessWidget/StatefulWidget count");
    }

    private static void Configurations(Action<bool, string> check)
    {
        var devices = PreviewConfigurations.ParseDevices(PreviewConfigurations.DefaultDevices);
        check(devices.Count == 3 && devices[0].Name == "Phone" && devices[0].Width == 390 && devices[0].Height == 844, "Default device presets");
        check(PreviewConfigurations.ParseDevices("Watch = 200 x 200; Foldable=673\u00d7841;").Count == 2, "Presets with spaces and the multiplication sign");
        foreach (var bad in new[] { "Phone", "Phone=10x10", "fit=300x300", "A=300x300;a=400x400", "Phone=390by844" })
            check(Throws(() => PreviewConfigurations.ParseDevices(bad)), "Invalid device preset rejected: " + bad);
        var scales = PreviewConfigurations.ParseTextScales("2.0, 1.3, 1.3");
        check(scales.SequenceEqual(new[] { 1.0, 1.3, 2.0 }), "Text scales sorted, unique, 1.0 always present");
        check(Throws(() => PreviewConfigurations.ParseTextScales("1,3")) == false && Throws(() => PreviewConfigurations.ParseTextScales("9")), "Text scale range (\"1,3\" is two entries)");
        check(PreviewConfigurations.ParseLocales("en_US, ar, en-US").SequenceEqual(new[] { "en-US", "ar" }) && Throws(() => PreviewConfigurations.ParseLocales("en US")), "Locales normalized and validated");

        var single = PreviewConfigurations.Expand(new PreviewSelection(), devices, scales, new[] { "ar" }, out var truncated);
        check(single.Count == 1 && single[0].Width == null && single[0].Theme == PreviewTheme.Ide && single[0].Label == "Preview" && !truncated, "Default selection: one preview filling the window, IDE theme");
        var phoneDark = PreviewConfigurations.Expand(new PreviewSelection { Device = "phone", Theme = "dark", TextScale = "1.3", Locale = "ar" }, devices, scales, new[] { "ar" }, out _);
        check(phoneDark.Count == 1 && phoneDark[0].Width == 390 && phoneDark[0].Height == 844 && phoneDark[0].Theme == PreviewTheme.Dark && phoneDark[0].TextScale == 1.3 && phoneDark[0].Locale == "ar"
              && phoneDark[0].Label == "Phone \u00b7 Dark \u00b7 1.3\u00d7 \u00b7 ar", "Phone, dark, 1.3x, ar: " + phoneDark[0].Label);
        var both = PreviewConfigurations.Expand(new PreviewSelection { Theme = "both", TextScale = "compare" }, devices, scales, new string[0], out truncated);
        check(both.Count == 6 && both.Count(c => c.Theme == PreviewTheme.Dark) == 3 && !truncated, "Light and dark times three text scales side by side");
        var many = PreviewConfigurations.Expand(new PreviewSelection { Device = "compare", Theme = "both", TextScale = "compare" }, devices, scales, new string[0], out truncated);
        check(many.Count == PreviewConfigurations.MaximumSideBySide && truncated, "At most six side by side");
        var custom = PreviewConfigurations.Expand(new PreviewSelection { Device = "custom", CustomWidth = 50, CustomHeight = 9000 }, devices, scales, new string[0], out _);
        check(custom[0].Width == 100 && custom[0].Height == 7680, "Custom size clamped");
        check(PreviewConfigurations.Expand(new PreviewSelection { Device = "Removed", TextScale = "nan", Locale = "x y" }, devices, scales, new string[0], out _)[0].Width == null, "Unknown saved values fall back to defaults");
        var script = phoneDark[0].DocumentScript();
        check(script.Contains("'20.8px'") && script.Contains("['ar']") && script.Contains("var p=Navigator.prototype") && script.Contains("defineProperty(p,'languages'"), "Document script: root font size 16 x 1.3 and navigator.languages");
        check(!single[0].DocumentScript().Contains("var langs") && single[0].DocumentScript().Contains("__vistudioLanguages") && single[0].DocumentScript().Contains("'16px'"),
            "Without a locale the browser's languages are kept (restored after an earlier override)");
        var live = phoneDark[0].LiveScript();
        check(live.Contains("'20.8px'") && live.Contains("dispatchEvent(new Event('languagechange'))") && !live.Contains("readystatechange") && !phoneDark[0].DocumentScript().Contains("dispatchEvent"),
            "Live script: applied to a running page (Flutter follows the root font size and languagechange), no reload");
        check(phoneDark[0].ProfileKey(false) == "dark" && single[0].ProfileKey(true) == "dark" && single[0].ProfileKey(false) == "light", "Profiles by effective theme");

        var state = new PreviewWindowState();
        state.Apply(new PreviewSelection { Device = "Tablet", Theme = "light", TextScale = "2.0", Locale = "de-DE" });
        var restored = state.ToSelection();
        check(restored.Device == "Tablet" && restored.Theme == "light" && restored.TextScale == "2.0" && restored.Locale == "de-DE", "Window state round trip");
    }

    private static void QuickFix(Action<bool, string> check)
    {
        const string source = "import 'package:flutter/material.dart';\n\nclass Counter extends StatefulWidget {\n  const Counter({super.key});\n  @override\n  State<Counter> createState() => _S();\n}\n\nclass _S extends State<Counter> {\n  @override\n  Widget build(BuildContext context) => const Text('0');\n}\n";
        var file = WidgetPreviewScanner.Scan(source);
        var counter = file.Widgets.Single(w => w.Name == "Counter");
        var fixedText = PreviewQuickFix.Apply(source, PreviewQuickFix.AddPreview(source, file, counter));
        check(fixedText.Contains("import 'package:flutter/material.dart';\nimport 'package:flutter/widget_previews.dart';\n"), "Quick fix adds the widget_previews import after the imports");
        check(fixedText.Contains("}\n\n@Preview(name: 'Counter')\nWidget counterPreview() => const Counter();\n\nclass _S"), "Quick fix adds a @Preview function after the class: " + fixedText);
        var rescanned = WidgetPreviewScanner.Scan(fixedText);
        check(rescanned.Targets.Single().Name == "counterPreview" && rescanned.Targets.Single().Problem == null, "The fixed file has a valid preview");
        var again = PreviewQuickFix.Apply(fixedText, PreviewQuickFix.AddPreview(fixedText, rescanned, rescanned.Widgets.Single(w => w.Name == "Counter")));
        check(again.Contains("Widget counterPreview2()") && again.Split(new[] { "widget_previews.dart" }, StringSplitOptions.None).Length == 2, "Second fix: unique name, no duplicate import");
        var crlf = "import 'package:flutter/widget_previews.dart' as wp;\r\nclass A extends StatelessWidget {\r\n  const A();\r\n}\r\n";
        var crlfFile = WidgetPreviewScanner.Scan(crlf);
        var crlfFixed = PreviewQuickFix.Apply(crlf, PreviewQuickFix.AddPreview(crlf, crlfFile, crlfFile.Widgets.Single()));
        check(crlfFixed.Contains("\r\n\r\n@wp.Preview(name: 'A')\r\nWidget aPreview() => const A();\r\n"), "CRLF files and prefixed imports");
        var needs = WidgetPreviewScanner.Scan("class N extends StatelessWidget { const N(this.v); final int v; }");
        check(Throws<InvalidOperationException>(() => PreviewQuickFix.AddPreview("", needs, needs.Widgets.Single())), "No quick fix without a no-argument constructor");
    }

    private static void HostProject(string root, Action<bool, string> check)
    {
        var package = Path.Combine(root, "my app");
        Write(Path.Combine(package, "lib", "widgets", "badge.dart"), "class Badge {}");
        var host = new PreviewHostProject(package, "my_app", Path.Combine(root, "host"));
        check(host.ImportUri(Path.Combine(package, "lib", "widgets", "badge.dart")) == "package:my_app/widgets/badge.dart", "Import URI of a lib file");
        check(Throws<InvalidOperationException>(() => host.ImportUri(Path.Combine(package, "test", "a_test.dart"))), "Files outside lib cannot be wrapped");
        host.Add(new WrappedWidget(Path.Combine(package, "lib", "widgets", "badge.dart"), "StatusBadge", "const StatusBadge()"));
        host.Add(new WrappedWidget(Path.Combine(package, "lib", "widgets", "badge.dart"), "Other", "Other.sample()"));
        check(Throws(() => host.Add(new WrappedWidget(Path.Combine(package, "lib", "widgets", "badge.dart"), "X", "X(); evil()"))), "Unexpected construction text rejected");
        const string userPubspec = "name: my_app\nenvironment:\n  sdk: \">=3.9.0 <4.0.0\"\n  flutter: \">=3.35.0\"\ndependencies:\n  flutter:\n    sdk: flutter\n";
        var changed = host.Write(userPubspec);
        var pubspec = File.ReadAllText(Path.Combine(host.Folder, "pubspec.yaml"));
        check(changed && pubspec.Contains("name: vistudio_preview_host") && pubspec.Contains("  sdk: \">=3.9.0 <4.0.0\"\n  flutter: \">=3.35.0\"") && pubspec.Contains("  my_app:\n    path: '" + Path.GetFullPath(package).Replace('\\', '/') + "'"),
            "Host pubspec: copied environment, path dependency on the user's package: " + pubspec);
        check(FlutterPubspec.HasFlutterDependency(pubspec), "The host is a Flutter package");
        var previews = File.ReadAllText(host.PreviewsPath);
        check(previews.Contains("import 'package:my_app/widgets/badge.dart' as w0;") && previews.Contains("Widget vistudioPreview0() => w0.Other.sample();")
              && previews.Contains("Widget vistudioPreview1() => const w0.StatusBadge();") && previews.Contains("@Preview(name: 'Other', group: 'badge.dart')"), "Generated previews (most recent first): " + previews);
        var scanned = WidgetPreviewScanner.Scan(previews);
        check(scanned.Targets.Count == 2 && scanned.Targets.All(t => t.Problem == null), "Generated previews are valid @Preview functions");
        check(!host.Write(userPubspec), "Writing again with the same pubspec reports no pubspec change (no new pub get)");
        host.Touch();
        check(File.ReadAllText(host.PreviewsPath).Contains("// Revision 1"), "Touch writes a new revision (the previewer's watcher reloads)");
        check(File.ReadAllText(Path.Combine(package, "lib", "widgets", "badge.dart")) == "class Badge {}", "The user's sources are untouched");
        var folder = PreviewHostProject.DefaultFolder(package, "my_app");
        check(folder.StartsWith(Path.Combine(Path.GetTempPath(), "Vistudio", "flutter-preview"), StringComparison.OrdinalIgnoreCase) && folder == PreviewHostProject.DefaultFolder(package + "\\", "my_app"),
            "Host folder under %TEMP%, stable per package");
    }

    private static void Events(Action<bool, string> check)
    {
        var started = PreviewerEvent.Parse("[{\"event\":\"widget_preview.started\",\"params\":{\"url\":\"http://localhost:5555\"}}]");
        check(started?.Kind == PreviewerEventKind.Started && started.Url == "http://localhost:5555", "started event with the URL");
        check(PreviewerEvent.Parse("[{\"event\":\"widget_preview.initializing\",\"params\":{\"pid\":42}}]")?.Pid == 42, "initializing event with the pid");
        PreviewerStatus Status(string level, string message) =>
            PreviewerEvent.Parse(new JArray(new JObject { ["event"] = "widget_preview.logMessage", ["params"] = new JObject { ["level"] = level, ["message"] = message } }).ToString())!.Status;
        check(Status("status", "Triggering reload based on update to script: [file:///C:/a.dart]") == PreviewerStatus.ReloadStarted, "Reload triggered");
        check(Status("status", "Reloaded application in 412ms.") == PreviewerStatus.Reloaded && Status("status", "Restarted application in 1,203ms.") == PreviewerStatus.Restarted, "Reload and restart done");
        check(Status("error", "Hot reload failed:") == PreviewerStatus.ReloadFailed && Status("error", "Hot reload rejected: const class changed") == PreviewerStatus.ReloadFailed, "Hot reload failure");
        check(Status("status", "Recompile complete. Page requires refresh.") == PreviewerStatus.PageRefreshRequired, "Page refresh request");
        check(Status("warning", "WebSocketProxyService: Hot restart failed: TimeoutException after 0:00:15.000000: Hot restart timed out - received 1/3 responses") == PreviewerStatus.RestartFailed,
            "A hot restart that did not reach every page (the window reloads them)");
        check(Status("status", "Done loading previews.") == PreviewerStatus.Ready, "Previews loaded");
        check(Status("error", "lib/a.dart:3:7: Error: Expected ';' after this.") == PreviewerStatus.Error, "Compiler error");
        check(Status("warning", "Lost connection to the Dart Tooling Daemon (DTD). Live preview updates are paused.") == PreviewerStatus.ConnectionLost, "DTD connection lost");
        check(Status("status", "Launching the Widget Preview Scaffold on Web Server...") == PreviewerStatus.None, "Other status lines are plain log");
        check(PreviewerEvent.Parse("Resolving dependencies...") == null && PreviewerEvent.Parse("[{\"event\":\"app.start\"}]") == null, "Non-previewer lines are not events");
        check(FlutterPreviewSession.UnsupportedMessage("Could not find a subcommand named \"widget-preview\" for \"flutter\".")!.Contains("flutter upgrade"), "Missing widget-preview command explained");
        check(FlutterPreviewSession.UnsupportedMessage("Could not find an option named \"--dtd-url\".")!.Contains("--dtd-url"), "Missing option explained");
        check(FlutterPreviewSession.UnsupportedMessage("Widget Previews requires Flutter Web to be enabled.")!.Contains("--enable-web"), "Web disabled explained");

        var sdk = new DartSdk(@"C:\sdk\flutter\bin\cache\dart-sdk\bin\dart.exe", @"C:\sdk\flutter");
        var start = DartCommandFactory.WidgetPreviewStart(sdk, @"C:\work\app", "ws://127.0.0.1:5000/abc=", verbose: true);
        check(start.Arguments.EndsWith("\"widget-preview\" \"start\" \"--machine\" \"--web-server\" \"--dtd-url=ws://127.0.0.1:5000/abc=\" \"--verbose\"\"", StringComparison.Ordinal) && start.WorkingDirectory == @"C:\work\app",
            "flutter widget-preview start command line: " + start.Arguments);
        check(DartCommandFactory.ToolingDaemon(sdk, @"C:\t").Arguments == "tooling-daemon --machine", "dart tooling-daemon --machine");
        check(DartCommandFactory.PreviewAnalysisServer(sdk, @"C:\work\app").Arguments.Contains("--disable-server-feature-completion"), "Preview analysis server without completion");
    }

    private static async Task Framing(Action<bool, string> check)
    {
        var frame = LspStdioClient.Frame(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = "h\u00e9" });
        var header = Encoding.ASCII.GetString(frame, 0, frame.Length);
        check(header.StartsWith("Content-Length: " + Encoding.UTF8.GetByteCount("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"h\u00e9\"}") + "\r\n\r\n", StringComparison.Ordinal), "LSP frame length counts UTF-8 bytes");
        var stream = new MemoryStream();
        stream.Write(frame, 0, frame.Length);
        var second = LspStdioClient.Frame(new JObject { ["jsonrpc"] = "2.0", ["method"] = "window/logMessage", ["params"] = new JObject { ["message"] = "x" } });
        stream.Write(second, 0, second.Length);
        stream.Position = 0;
        var messages = new List<JObject>();
        await LspStdioClient.ReadFramesAsync(new TrickleStream(stream.ToArray(), 7), messages.Add);
        check(messages.Count == 2 && (string?)messages[0]["result"] == "h\u00e9" && (string?)messages[1]["method"] == "window/logMessage", "LSP frames read across 7-byte chunks");
    }

    private static void Settings(Action<bool, string> check)
    {
        var settings = new DartSettings();
        check(settings.WidgetPreview && settings.WidgetPreviewCodeLens && settings.WidgetPreviewReload == WidgetPreviewReloadTrigger.OnSave && settings.WidgetPreviewPubGet, "Widget preview defaults");
        settings.Validate();
        settings.WidgetPreviewReloadDelay = 50;
        check(Throws(settings.Validate), "Reload delay below 200 ms rejected");
        settings.WidgetPreviewReloadDelay = 1000;
        settings.WidgetPreviewDevices = "Phone=1x1";
        check(Throws(settings.Validate), "Invalid device preset rejected by the settings");
    }

    /// <summary>A stream that returns at most <c>chunk</c> bytes per read.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int chunk;
        public TrickleStream(byte[] data, int chunk) : base(data) { this.chunk = chunk; }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token) => Task.FromResult(Read(buffer, offset, count));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool Throws(Action action) => Throws<ArgumentException>(action);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
