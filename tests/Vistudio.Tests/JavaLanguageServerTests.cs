using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Vistudio;
using Vistudio.Core;

internal static class JavaLanguageServerTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var install = Path.Combine(root, "jdt ls");
        var plugins = Path.Combine(install, "plugins");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(Path.Combine(install, "config_win"));
        var jdk = Path.Combine(root, "jdk 21");
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        File.WriteAllText(Path.Combine(jdk, "bin", "java.exe"), "");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);

        void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); check(false, message); } catch (T) { check(true, message); }
        }
        Throws<DirectoryNotFoundException>(() => JavaLanguageServer.Create("", jdk, workspace, root), "Unset JDT LS folder is rejected");
        Throws<DirectoryNotFoundException>(() => JavaLanguageServer.Create("relative", jdk, workspace, root), "Relative JDT LS folder is rejected");
        Throws<FileNotFoundException>(() => JavaLanguageServer.Create(install, jdk, workspace, root), "Missing equinox launcher is rejected");
        File.WriteAllText(Path.Combine(plugins, "org.eclipse.equinox.launcher_1.6.900.v20240613-2009.jar"), "");
        File.WriteAllText(Path.Combine(plugins, "org.eclipse.equinox.launcher_1.7.0.v20250424-1814.jar"), "");
        File.WriteAllText(Path.Combine(plugins, "org.eclipse.equinox.launcher.win32.win32.x86_64_1.2.0.jar"), "");
        check(JavaLanguageServer.FindLauncher(install)!.EndsWith("launcher_1.7.0.v20250424-1814.jar", StringComparison.Ordinal), "Newest generic equinox launcher is selected");
        Throws<DirectoryNotFoundException>(() => JavaLanguageServer.Create(install, "", workspace, root), "Missing JDT LS runtime is rejected");
        Throws<FileNotFoundException>(() => JavaLanguageServer.Create(install, root, workspace, root), "Runtime without java.exe is rejected");

        var data = Path.Combine(root, "data");
        var start = JavaLanguageServer.Create(install, jdk, workspace, data);
        check(start.FileName == Path.Combine(jdk, "bin", "java.exe") && start.WorkingDirectory == workspace, "Runs the selected Java runtime in the opened folder");
        check(start.Arguments.Contains("-Declipse.application=org.eclipse.jdt.ls.core.id1") && start.Arguments.Contains("\"-Dosgi.sharedConfiguration.area=" + Path.Combine(install, "config_win") + "\""), "JDT LS application and quoted shared configuration");
        check(start.Arguments.Contains("-Djava.import.generatesMetadataFilesAtProjectRoot=false"), "No Eclipse metadata in the project folder");
        check(JavaLanguageServer.InitScriptArgument(Path.Combine(root, "a b", "x.gradle")) == "--init-script \"" + Path.Combine(root, "a b", "x.gradle") + "\"", "Quoted init script argument");
        check(start.Arguments.Contains("-jar \"" + Path.Combine(plugins, "org.eclipse.equinox.launcher_1.7.0.v20250424-1814.jar") + "\""), "Quoted launcher jar");
        check(start.Arguments.EndsWith("-data \"" + JavaLanguageServer.DataDirectory(data, workspace) + "\"", StringComparison.Ordinal) && JavaLanguageServer.DataDirectory(data, workspace).StartsWith(data, StringComparison.Ordinal), "Per-folder quoted data directory outside the sources");
        check(start.RedirectStandardInput && start.RedirectStandardOutput && !start.UseShellExecute && start.EnvironmentVariables["JAVA_HOME"] == jdk, "Stdio process with its own JAVA_HOME");
        check(JavaLanguageServer.DataDirectory(data, workspace) == JavaLanguageServer.DataDirectory(data, workspace.ToLowerInvariant() + "\\"), "Data directory is stable per folder");
        check(JavaLanguageServer.DataDirectory(data, workspace) != JavaLanguageServer.DataDirectory(data, root), "Different folders get different data directories");

        var server = Path.Combine(root, "kotlin-lsp", "bin", "intellij-server.exe");
        Directory.CreateDirectory(Path.Combine(root, "kotlin-lsp", "jbr", "bin"));
        File.WriteAllText(Path.Combine(root, "kotlin-lsp", "jbr", "bin", "java.exe"), "");
        check(JavaLanguageServer.ResolveJavaHome("C:/a", "C:/b", server) == "C:/a", "Explicit JDT LS runtime wins");
        check(JavaLanguageServer.ResolveJavaHome("", "C:/b", server) == "C:/b", "Kotlin server JDK is the next choice");
        check(JavaLanguageServer.ResolveJavaHome("", "", server) == Path.Combine(root, "kotlin-lsp", "jbr"), "Runtime bundled with the Kotlin server is the fallback");
        check(JavaLanguageServer.ResolveJavaHome("", "", "") == "", "No runtime when nothing is configured");

        // Visual Studio never enables Go To Definition for dynamically registered JDT LS features.
        var initialize = JObject.Parse(@"{""processId"":1,""capabilities"":{""textDocument"":{""definition"":{""dynamicRegistration"":true,""linkSupport"":true},""completion"":{""dynamicRegistration"":true,""completionItem"":{""snippetSupport"":true}}},""workspace"":{""didChangeWatchedFiles"":{""dynamicRegistration"":true}}}}");
        var normalized = (JObject)JavaLspCompatibility.NormalizeInitialize(initialize);
        check((bool?)normalized["capabilities"]!["textDocument"]!["definition"]!["dynamicRegistration"] == false && (bool?)normalized["capabilities"]!["textDocument"]!["definition"]!["linkSupport"] == true, "Definition becomes static, other options kept");
        check((bool?)normalized["capabilities"]!["textDocument"]!["completion"]!["dynamicRegistration"] == false && (bool?)normalized["capabilities"]!["textDocument"]!["completion"]!["completionItem"]!["snippetSupport"] == true, "Completion becomes static, item options kept");
        check((bool?)normalized["capabilities"]!["textDocument"]!["references"]!["dynamicRegistration"] == false, "Absent features are declared static");
        check((bool?)normalized["capabilities"]!["workspace"]!["didChangeWatchedFiles"]!["dynamicRegistration"] == true && (int?)normalized["processId"] == 1, "Workspace capabilities and other parameters are unchanged");
        check((bool?)((JObject)JavaLspCompatibility.NormalizeInitialize(new JObject()))["capabilities"]!["textDocument"]!["hover"]!["dynamicRegistration"] == false, "Missing capabilities object is created");
    }
}
