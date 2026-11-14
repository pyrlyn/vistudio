using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Vistudio.Core.Projects;

namespace Vistudio.Core.Templates
{
    /// <summary>Gradle project layouts offered in Visual Studio's New Project dialog.</summary>
    public enum KotlinTemplateKind
    {
        JvmConsole,
        JvmLibrary,
        KotlinJava,
        Multiplatform,
        ComposeDesktop,
    }

    public sealed class KotlinTemplateOptions
    {
        public KotlinTemplateOptions(KotlinTemplateKind kind, string projectName, IEnumerable<string>? targets = null)
        {
            Kind = kind;
            ProjectName = KotlinProjectTemplate.GradleProjectName(projectName);
            PackageName = KotlinProjectTemplate.PackageFor(projectName);
            var chosen = (targets ?? KotlinProjectTemplate.MultiplatformTargets).ToArray();
            if (kind == KotlinTemplateKind.Multiplatform)
            {
                var unknown = chosen.Where(t => !KotlinProjectTemplate.MultiplatformTargets.Contains(t)).ToArray();
                if (unknown.Length != 0) throw new ArgumentException("Unknown Kotlin Multiplatform target: " + string.Join(", ", unknown));
                // Canonical order keeps generated scripts stable whatever order the targets were picked in.
                Targets = KotlinProjectTemplate.MultiplatformTargets.Where(chosen.Contains).ToArray();
                if (Targets.Count == 0) throw new ArgumentException("Choose at least one Kotlin Multiplatform target.");
            }
            else Targets = Array.Empty<string>();
        }

        public KotlinTemplateKind Kind { get; }
        public string ProjectName { get; }
        public string PackageName { get; }
        /// <summary>Kotlin Multiplatform targets in canonical order; empty for other kinds.</summary>
        public IReadOnlyList<string> Targets { get; }
    }

    /// <summary>
    /// Generates new Gradle projects for the Visual Studio templates. Versions match the validated samples
    /// (Gradle 8.14.7, Kotlin 2.4.21, Compose Multiplatform 1.12.2); Gradle stays the build system.
    /// </summary>
    public static class KotlinProjectTemplate
    {
        public const string GradleVersion = "8.14.7";
        public const string GradleDistributionSha256 = "903bfc4bc7622323124054c10f509246212a1902948bdaf26c379cca8735e856";
        public const string KotlinVersion = "2.4.21";
        public const string ComposeVersion = "1.12.2";
        /// <summary>Root element of the stub project file inside a .vstemplate.</summary>
        public const string StubElement = "VistudioTemplate";
        public static readonly IReadOnlyList<string> MultiplatformTargets = new[] { "jvm", "js", "wasmJs", "mingwX64" };
        /// <summary>Files the wrapper directory must provide; they are copied unchanged.</summary>
        public static readonly IReadOnlyList<string> WrapperFiles = new[] { "gradlew.bat", "gradle/wrapper/gradle-wrapper.jar" };

        private static readonly HashSet<string> KotlinKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface", "is", "null",
            "object", "package", "return", "super", "this", "throw", "true", "try", "typealias", "typeof", "val", "var", "when", "while",
            // Java keywords matter for the Kotlin + Java template.
            "abstract", "assert", "boolean", "byte", "case", "catch", "char", "const", "default", "double", "enum", "extends",
            "final", "finally", "float", "goto", "implements", "import", "instanceof", "int", "long", "native", "new", "private",
            "protected", "public", "short", "static", "strictfp", "switch", "synchronized", "throws", "transient", "void", "volatile",
        };

        /// <summary>A lowercase package name from the project name: ASCII letters and digits, never a keyword, never empty.</summary>
        public static string PackageFor(string projectName)
        {
            var name = new string((projectName ?? "").ToLowerInvariant().Where(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9').ToArray());
            if (name.Length == 0) return "app";
            if (char.IsDigit(name[0])) name = "app" + name;
            return KotlinKeywords.Contains(name) ? name + "app" : name;
        }

        /// <summary>The root project name without characters Gradle rejects in project names.</summary>
        public static string GradleProjectName(string projectName)
        {
            var name = Regex.Replace((projectName ?? "").Trim(), @"[/\\:<>""?*|\p{Cc}]", "-").Trim('.', ' ');
            return name.Length == 0 ? "kotlin-app" : name;
        }

        public static string BuildTasks(KotlinTemplateOptions options) => "build";

        public static string TestTasks(KotlinTemplateOptions options) =>
            options.Kind == KotlinTemplateKind.Multiplatform || options.Kind == KotlinTemplateKind.ComposeDesktop ? "allTests" : "test";

        /// <summary>The project's Run task; a library has nothing to run, so Run builds it.</summary>
        public static string RunTasks(KotlinTemplateOptions options)
        {
            switch (options.Kind)
            {
                case KotlinTemplateKind.JvmLibrary: return "build";
                case KotlinTemplateKind.Multiplatform:
                    switch (options.Targets[0])
                    {
                        case "jvm": return "jvmRun";
                        case "js": return "jsNodeDevelopmentRun";
                        case "wasmJs": return "wasmJsNodeDevelopmentRun";
                        default: return "runDebugExecutableMingwX64";
                    }
                default: return "run";
            }
        }

        /// <summary>Text files of the new project keyed by relative path with forward slashes (wrapper binaries excluded).</summary>
        public static IReadOnlyDictionary<string, string> Files(KotlinTemplateOptions options)
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pkg = options.PackageName;
            files[".gitignore"] = Lines(".gradle/", ".kotlin/", "build/", ".vs/", "*.user");
            files["gradle.properties"] = Lines("kotlin.code.style=official");
            files["gradle/wrapper/gradle-wrapper.properties"] = Lines(
                "distributionBase=GRADLE_USER_HOME",
                "distributionPath=wrapper/dists",
                @"distributionUrl=https\://services.gradle.org/distributions/gradle-" + GradleVersion + "-bin.zip",
                "distributionSha256Sum=" + GradleDistributionSha256,
                "networkTimeout=10000",
                "validateDistributionUrl=true",
                "zipStoreBase=GRADLE_USER_HOME",
                "zipStorePath=wrapper/dists");
            var rootName = "rootProject.name = " + KotlinString(options.ProjectName);
            switch (options.Kind)
            {
                case KotlinTemplateKind.JvmConsole:
                case KotlinTemplateKind.KotlinJava:
                    files["settings.gradle.kts"] = Lines(rootName);
                    files["build.gradle.kts"] = JvmBuild(application: pkg + ".MainKt");
                    if (options.Kind == KotlinTemplateKind.KotlinJava)
                    {
                        files[$"src/main/java/{pkg}/Greeter.java"] = Lines(
                            $"package {pkg};",
                            "",
                            "/** Java code compiled together with the Kotlin sources of the same module. */",
                            "public final class Greeter {",
                            "    private final String greeting;",
                            "",
                            "    public Greeter(String greeting) {",
                            "        this.greeting = greeting;",
                            "    }",
                            "",
                            "    public String greet(String name) {",
                            "        return greeting + \", \" + name + \"!\";",
                            "    }",
                            "}");
                        files[$"src/main/kotlin/{pkg}/Main.kt"] = Lines(
                            $"package {pkg}",
                            "",
                            "fun greeting(name: String): String = Greeter(\"Hello\").greet(name)",
                            "",
                            "fun main() {",
                            "    println(greeting(\"Kotlin and Java\"))",
                            "}");
                        files[$"src/test/kotlin/{pkg}/GreeterTest.kt"] = Test(pkg, "GreeterTest", "greetsFromJava",
                            "assertEquals(\"Hello, Java!\", greeting(\"Java\"))");
                    }
                    else
                    {
                        files[$"src/main/kotlin/{pkg}/Main.kt"] = Lines(
                            $"package {pkg}",
                            "",
                            "fun greeting(name: String): String = \"Hello, $name!\"",
                            "",
                            "fun main() {",
                            "    println(greeting(\"Kotlin\"))",
                            "}");
                        files[$"src/test/kotlin/{pkg}/MainTest.kt"] = Test(pkg, "MainTest", "greetsByName",
                            "assertEquals(\"Hello, Kotlin!\", greeting(\"Kotlin\"))");
                    }
                    break;
                case KotlinTemplateKind.JvmLibrary:
                    files["settings.gradle.kts"] = Lines(rootName);
                    files["build.gradle.kts"] = JvmBuild(application: null);
                    files[$"src/main/kotlin/{pkg}/Library.kt"] = Lines(
                        $"package {pkg}",
                        "",
                        "/** Public API of the library. */",
                        "class Library {",
                        "    fun greeting(name: String): String = \"Hello, $name!\"",
                        "}");
                    files[$"src/test/kotlin/{pkg}/LibraryTest.kt"] = Test(pkg, "LibraryTest", "greetsByName",
                        "assertEquals(\"Hello, Kotlin!\", Library().greeting(\"Kotlin\"))");
                    break;
                case KotlinTemplateKind.Multiplatform:
                    files["settings.gradle.kts"] = Lines(rootName);
                    files["build.gradle.kts"] = MultiplatformBuild(options);
                    files[$"src/commonMain/kotlin/{pkg}/Main.kt"] = Lines(
                        $"package {pkg}",
                        "",
                        "fun greeting(name: String): String = \"Hello, $name!\"",
                        "",
                        "fun main() {",
                        "    println(greeting(platformName()))",
                        "}");
                    files[$"src/commonMain/kotlin/{pkg}/Platform.kt"] = Lines(
                        $"package {pkg}",
                        "",
                        "/** Implemented in each target's source set. */",
                        "expect fun platformName(): String");
                    foreach (var target in options.Targets)
                        files[$"src/{target}Main/kotlin/{pkg}/Platform.{target}.kt"] = Lines(
                            $"package {pkg}",
                            "",
                            "actual fun platformName(): String = " + KotlinString(PlatformDisplayName(target)));
                    files[$"src/commonTest/kotlin/{pkg}/GreetingTest.kt"] = Test(pkg, "GreetingTest", "greetsThePlatform",
                        "assertEquals(\"Hello, \" + platformName() + \"!\", greeting(platformName()))");
                    break;
                case KotlinTemplateKind.ComposeDesktop:
                    files["settings.gradle.kts"] = ComposeSettings(rootName);
                    files["build.gradle.kts"] = ComposeBuild(pkg);
                    files[$"src/commonMain/kotlin/{pkg}/App.kt"] = Lines(
                        $"package {pkg}",
                        "",
                        "import androidx.compose.foundation.background",
                        "import androidx.compose.foundation.clickable",
                        "import androidx.compose.foundation.layout.Column",
                        "import androidx.compose.foundation.layout.padding",
                        "import androidx.compose.foundation.shape.RoundedCornerShape",
                        "import androidx.compose.foundation.text.BasicText",
                        "import androidx.compose.runtime.Composable",
                        "import androidx.compose.runtime.getValue",
                        "import androidx.compose.runtime.mutableStateOf",
                        "import androidx.compose.runtime.remember",
                        "import androidx.compose.runtime.setValue",
                        "import androidx.compose.ui.Modifier",
                        "import androidx.compose.ui.graphics.Color",
                        "import androidx.compose.ui.text.TextStyle",
                        "import androidx.compose.ui.tooling.preview.Preview",
                        "import androidx.compose.ui.unit.dp",
                        "import androidx.compose.ui.unit.sp",
                        "",
                        "fun greeting(name: String): String = \"Hello, $name!\"",
                        "",
                        "@Composable",
                        "fun App() {",
                        "    var clicks by remember { mutableStateOf(0) }",
                        "    Column(",
                        "        Modifier",
                        "            .background(Color(0xFF7F52FF), RoundedCornerShape(12.dp))",
                        "            .clickable { clicks++ }",
                        "            .padding(16.dp)",
                        "    ) {",
                        "        BasicText(greeting(\"Compose\"), style = TextStyle(color = Color.White, fontSize = 20.sp))",
                        "        BasicText(\"Clicks: $clicks\", style = TextStyle(color = Color.White, fontSize = 14.sp))",
                        "    }",
                        "}",
                        "",
                        "@Preview",
                        "@Composable",
                        "fun AppPreview() {",
                        "    App()",
                        "}");
                    files[$"src/desktopMain/kotlin/{pkg}/Main.kt"] = Lines(
                        $"package {pkg}",
                        "",
                        "import androidx.compose.ui.window.Window",
                        "import androidx.compose.ui.window.application",
                        "",
                        "fun main() = application {",
                        "    Window(onCloseRequest = ::exitApplication, title = " + KotlinString(options.ProjectName) + ") {",
                        "        App()",
                        "    }",
                        "}");
                    files[$"src/commonTest/kotlin/{pkg}/GreetingTest.kt"] = Test(pkg, "GreetingTest", "greetsByName",
                        "assertEquals(\"Hello, Compose!\", greeting(\"Compose\"))");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.Kind, "Unknown Kotlin template.");
            }
            return files;
        }

        /// <summary>
        /// Writes the project into <paramref name="folder"/> (created when missing) and returns its saved vistudio.kotlinproj.
        /// Existing files are never replaced. <paramref name="wrapperDirectory"/> supplies <see cref="WrapperFiles"/>.
        /// </summary>
        public static KotlinProjectFile Create(string folder, KotlinTemplateOptions options, string wrapperDirectory, string gradleJavaHome = "")
        {
            folder = Path.GetFullPath(folder);
            var text = Files(options);
            var all = text.Keys.Concat(WrapperFiles).Append(KotlinProjectFile.FileName).ToArray();
            var existing = all.Where(f => File.Exists(Path.Combine(folder, f))).ToArray();
            if (existing.Length != 0)
                throw new IOException("The folder already contains " + string.Join(", ", existing) + ". Choose an empty location for the new Kotlin project: " + folder);
            foreach (var wrapper in WrapperFiles)
                if (!File.Exists(Path.Combine(wrapperDirectory, wrapper)))
                    throw new FileNotFoundException("The Gradle wrapper template is missing: " + Path.Combine(wrapperDirectory, wrapper));
            foreach (var file in text)
            {
                var path = Path.Combine(folder, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Value, new UTF8Encoding(false));
            }
            foreach (var wrapper in WrapperFiles)
            {
                var path = Path.Combine(folder, wrapper);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(wrapperDirectory, wrapper), path);
            }
            var project = KotlinProjectFile.Create(folder, new KotlinSettings
            {
                GradleRoot = ".",
                GradleJavaHome = gradleJavaHome ?? "",
                BuildTasks = BuildTasks(options),
                TestTasks = TestTasks(options),
                RunTasks = RunTasks(options),
            });
            project.Save();
            return project;
        }

        /// <summary>The stub project file of a .vstemplate: the template kind plus the name typed in the New Project dialog.</summary>
        public static string Stub(KotlinTemplateKind kind, string projectName) =>
            new XDocument(new XElement(StubElement, new XAttribute("Kind", kind.ToString()), new XAttribute("ProjectName", projectName))).ToString();

        /// <summary>Reads a template stub; returns null for any other file (for example a real .kotlinproj).</summary>
        public static (KotlinTemplateKind Kind, string ProjectName)? TryReadStub(string path)
        {
            XDocument document;
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 }))
                    document = XDocument.Load(reader);
            }
            catch (XmlException) { return null; }
            if (document.Root?.Name != StubElement) return null;
            if (!Enum.TryParse<KotlinTemplateKind>((string?)document.Root.Attribute("Kind"), out var kind) || !Enum.IsDefined(typeof(KotlinTemplateKind), kind))
                throw new InvalidDataException("Unknown Kotlin project template: " + (string?)document.Root.Attribute("Kind"));
            var name = (string?)document.Root.Attribute("ProjectName") ?? "";
            if (name.Length == 0 || name.Contains("$")) name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
            return (kind, name);
        }

        public static string PlatformDisplayName(string target)
        {
            switch (target)
            {
                case "jvm": return "JVM";
                case "js": return "JavaScript";
                case "wasmJs": return "WebAssembly";
                case "mingwX64": return "Windows (Kotlin/Native)";
                default: return target;
            }
        }

        private static string JvmBuild(string? application)
        {
            var lines = new List<string> { "plugins {", $"    kotlin(\"jvm\") version \"{KotlinVersion}\"" };
            lines.Add(application != null ? "    application" : "    `java-library`");
            lines.AddRange(new[]
            {
                "}",
                "",
                "repositories { mavenCentral() }",
                "",
                "dependencies {",
                "    testImplementation(kotlin(\"test\"))",
                "}",
                "",
                "kotlin {",
                "    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11) }",
                "}",
                "tasks.withType<JavaCompile>().configureEach {",
                "    sourceCompatibility = \"11\"",
                "    targetCompatibility = \"11\"",
                "}",
                "tasks.test { useJUnitPlatform() }",
            });
            if (application != null) lines.Add($"application {{ mainClass.set(\"{application}\") }}");
            return Lines(lines.ToArray());
        }

        private static string MultiplatformBuild(KotlinTemplateOptions options)
        {
            var pkg = options.PackageName;
            var lines = new List<string> { $"plugins {{ kotlin(\"multiplatform\") version \"{KotlinVersion}\" }}", "", "repositories { mavenCentral() }", "", "kotlin {" };
            foreach (var target in options.Targets)
            {
                switch (target)
                {
                    case "jvm":
                        lines.AddRange(new[]
                        {
                            "    jvm {",
                            "        compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11) }",
                            $"        mainRun {{ mainClass.set(\"{pkg}.MainKt\") }}",
                            "    }",
                        });
                        break;
                    case "js": lines.Add("    js { nodejs(); binaries.executable() }"); break;
                    case "wasmJs": lines.Add("    wasmJs { nodejs(); binaries.executable() }"); break;
                    case "mingwX64": lines.Add($"    mingwX64 {{ binaries.executable {{ entryPoint = \"{pkg}.main\" }} }}"); break;
                }
            }
            lines.AddRange(new[]
            {
                "",
                "    sourceSets {",
                "        commonTest.dependencies { implementation(kotlin(\"test\")) }",
                "    }",
                "}",
            });
            return Lines(lines.ToArray());
        }

        private static string ComposeSettings(string rootName) => Lines(
            "pluginManagement {",
            "    repositories {",
            "        gradlePluginPortal()",
            "        mavenCentral()",
            "        google()",
            "    }",
            "    plugins {",
            $"        kotlin(\"multiplatform\") version \"{KotlinVersion}\"",
            $"        id(\"org.jetbrains.kotlin.plugin.compose\") version \"{KotlinVersion}\"",
            $"        id(\"org.jetbrains.compose\") version \"{ComposeVersion}\"",
            "    }",
            "}",
            "",
            "dependencyResolutionManagement {",
            "    repositories {",
            "        mavenCentral()",
            "        google()",
            "    }",
            "}",
            "",
            rootName);

        private static string ComposeBuild(string pkg) => Lines(
            "plugins {",
            "    kotlin(\"multiplatform\")",
            "    id(\"org.jetbrains.kotlin.plugin.compose\")",
            "    id(\"org.jetbrains.compose\")",
            "}",
            "",
            $"val composeVersion = \"{ComposeVersion}\"",
            "",
            "kotlin {",
            "    jvm(\"desktop\")",
            "",
            "    sourceSets {",
            "        commonMain.dependencies {",
            "            implementation(\"org.jetbrains.compose.runtime:runtime:$composeVersion\")",
            "            implementation(\"org.jetbrains.compose.foundation:foundation:$composeVersion\")",
            "            implementation(\"org.jetbrains.compose.ui:ui:$composeVersion\")",
            "            implementation(\"org.jetbrains.compose.ui:ui-tooling-preview:$composeVersion\")",
            "        }",
            "        commonTest.dependencies { implementation(kotlin(\"test\")) }",
            "        val desktopMain by getting {",
            "            dependencies { implementation(compose.desktop.currentOs) }",
            "        }",
            "    }",
            "}",
            "",
            "compose.desktop {",
            $"    application {{ mainClass = \"{pkg}.MainKt\" }}",
            "}");

        private static string Test(string pkg, string className, string method, string assertion) => Lines(
            $"package {pkg}",
            "",
            "import kotlin.test.Test",
            "import kotlin.test.assertEquals",
            "",
            $"class {className} {{",
            "    @Test",
            $"    fun {method}() {{",
            "        " + assertion,
            "    }",
            "}");

        /// <summary>A Kotlin string literal; escapes quotes, backslashes and string templates.</summary>
        public static string KotlinString(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";

        private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
    }
}
