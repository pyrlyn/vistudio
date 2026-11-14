using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    /// <summary>
    /// Builds the DAP launch arguments for the Kotlin server's JVM debugger (server 271+): the launch names the main
    /// class and its source file, and the server finds the owning module itself. Kotlin and Java share the session.
    /// No Visual Studio dependencies; linked into the regression tests.
    /// </summary>
    internal static class KotlinJvmLaunch
    {
        // Frame languages are mapped by source extension in Registration/KotlinDebugAdapter.pkgdef (LanguageMappings).
        internal const string KotlinLanguageId = "51298598-8a1a-4ec2-a777-42d617e9ad39";
        internal const string JavaLanguageId = "c1d95a48-5d0f-4c2b-9f43-3f6a1e0a7b21";

        internal static bool IsSourceFile(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".kt", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".java", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The JVM entry class of a Kotlin file with a top-level <c>main</c> (<c>pkg.FileKt</c>, or its <c>@file:JvmName</c>)
        /// or of a Java file declaring <c>public static void main</c> (<c>pkg.FileName</c>). Null when the file has none.
        /// Lets F5 on the current document work without a launch.vs.json entry.
        /// </summary>
        internal static string? InferMainClass(string path, string text)
        {
            var package = System.Text.RegularExpressions.Regex.Match(text, @"^\s*package\s+([\p{L}_][\p{L}\p{Nd}_]*(?:\.[\p{L}_][\p{L}\p{Nd}_]*)*)", System.Text.RegularExpressions.RegexOptions.Multiline);
            var prefix = package.Success ? package.Groups[1].Value + "." : "";
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(Path.GetExtension(path), ".java", StringComparison.OrdinalIgnoreCase))
                return System.Text.RegularExpressions.Regex.IsMatch(text, @"\bpublic\s+static\s+void\s+main\s*\(") ? prefix + name : null;
            if (!string.Equals(Path.GetExtension(path), ".kt", StringComparison.OrdinalIgnoreCase)) return null;
            // A top-level main starts at column 0; members of classes/objects are indented.
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:(?:public|internal|suspend)\s+)*fun\s+main\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline)) return null;
            var jvmName = System.Text.RegularExpressions.Regex.Match(text, @"^\s*@file\s*:\s*JvmName\s*\(\s*""([^""]+)""\s*\)", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (jvmName.Success) return prefix + jvmName.Groups[1].Value;
            return name.Length == 0 ? null : prefix + char.ToUpperInvariant(name[0]) + name.Substring(1) + "Kt";
        }

        /// <param name="buildTool">Result of <c>intellij.java.buildTool</c> for the source file: <c>{ tool, env }</c>.</param>
        internal static JObject Create(string workspace, string source, string mainClass, JObject config, JObject? buildTool, string? gradleJavaHome)
        {
            if (!IsSourceFile(source)) throw new InvalidOperationException("The debug entry file must be a .kt or .java source file.");
            var tool = (string?)buildTool?["tool"];
            if (tool != "gradle")
                throw new InvalidOperationException("The source file must belong to an imported Gradle JVM module" +
                    (string.IsNullOrEmpty(tool) ? "" : " (the server reports build tool '" + tool + "')") + ". Check Kotlin server import output.");
            var launch = new JObject
            {
                ["type"] = "intellij_gradle", ["tool"] = "gradle", ["request"] = "launch", ["name"] = mainClass,
                ["mainClass"] = mainClass, ["file"] = source, ["console"] = "internalConsole",
                ["cwd"] = Path.GetFullPath(Path.Combine(workspace, (string?)config["cwd"] ?? ".")),
                ["toolArgs"] = new JArray("--no-daemon", "--console=plain")
            };
            if (config["gradleProject"]?.Type == JTokenType.String) launch["projectPath"] = config["gradleProject"]!.DeepClone();
            if (config["sourceSet"]?.Type == JTokenType.String) launch["sourceSet"] = config["sourceSet"]!.DeepClone();
            foreach (var key in new[] { "args", "vmArgs", "noDebug" })
                if (config[key] != null) launch[key] = config[key]!.DeepClone();
            // Precedence: the configuration's env, then the Gradle JDK setting, then what the server detected.
            var env = (buildTool?["env"] as JObject)?.DeepClone() as JObject ?? new JObject();
            if (!string.IsNullOrWhiteSpace(gradleJavaHome)) env["JAVA_HOME"] = gradleJavaHome;
            if (config["env"] is JObject custom)
                foreach (var property in custom.Properties()) env[property.Name] = property.Value.DeepClone();
            if (env.Count != 0) launch["env"] = env;
            return launch;
        }
    }
}
