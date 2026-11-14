using System;
using System.IO;
using System.Linq;
using Vistudio.Core;
using Vistudio.Core.Projects;
using Vistudio.Core.Zig;

/// <summary>Per-window overrides from a workspace's .vs\settings.json (T50).</summary>
internal static class WorkspaceSettingsTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var a = Path.Combine(root, "window a");
        var b = Path.Combine(root, "window b");
        void Write(string folder, string text) { Directory.CreateDirectory(Path.Combine(folder, ".vs")); File.WriteAllText(WorkspaceSettings.FilePath(folder), text); }

        check(WorkspaceSettings.Read(null).Path == null && WorkspaceSettings.Read(Path.Combine(root, "none")).Values.Count == 0,
            "No workspace or no .vs\\settings.json means no overrides");

        Write(a, @"{
  // Visual Studio Code style: comments and trailing commas are accepted.
  ""vistudio.zig.zigPath"": ""tools/zig/zig.exe"",
  ""vistudio.zig.libDirectory"": ""${workspaceFolder}\\zig-lib"",
  ""vistudio.zig.DEFAULTFLAGS"": ""-O ReleaseSafe -lc"",
  ""vistudio"": { ""zig"": { ""astCheckOnSave"": false, ""diagnosticLevel"": ""errors"", ""astCheckDelayMilliseconds"": 300, }, },
  ""vistudio.java.languageServerPath"": ""jdtls"",
  ""vistudio.kotlin.gradleRoot"": ""sub"",
  ""vistudio.kotlin.rainbowBraces"": false,
  ""editor.tabSize"": 4, /* other tools' keys are ignored */
}");
        var file = WorkspaceSettings.Read(a);
        check(file.Path == WorkspaceSettings.FilePath(a) && file.Warnings.Count == 0 && file.Values.ContainsKey("vistudio.zig.astCheckOnSave"),
            "Comments, trailing commas and nested objects parse into flat keys");

        var zig = new ZigSettings { DefaultFlags = "-global" };
        var warnings = WorkspaceSettings.ApplyTo(zig, a);
        check(warnings.Count == 0, "Valid Zig overrides produce no warnings: " + string.Join("; ", warnings));
        check(zig.ZigPath == Path.Combine(a, "tools", "zig", "zig.exe") && zig.LibDirectory == Path.Combine(a, "zig-lib"),
            "Relative and ${workspaceFolder} compiler paths resolve against the workspace root");
        check(zig.DefaultFlags == "-O ReleaseSafe -lc" && !zig.AstCheckOnSave && zig.DiagnosticLevel == ZigDiagnosticLevel.Errors && zig.AstCheckDelayMilliseconds == 300,
            "Flags, booleans, enums (any case) and integers override the global values");
        check(zig.BuildFlags == "" && zig.AstCheckWhileTyping, "Keys the workspace does not set keep the global value");

        var kotlin = new KotlinSettings();
        WorkspaceSettings.ApplyTo(kotlin, a);
        check(kotlin.JavaLanguageServerPath == Path.Combine(a, "jdtls") && kotlin.GradleRoot == "sub" && !kotlin.RainbowBraces,
            "vistudio.java.* maps to the Java* properties; the Gradle root stays workspace-relative");
        check(zig.ZigPath != "" && kotlin.LanguageServerPath == "", "Kotlin and Zig sections do not leak into each other");

        // A second window with a different workspace gets its own compiler; neither file affects the other.
        Write(b, "{ \"vistudio.zig.zigPath\": \"C:\\\\zig-0.15\\\\zig.exe\", \"vistudio.zig.defaultFlags\": \"-O Debug\" }");
        var other = new ZigSettings();
        WorkspaceSettings.ApplyTo(other, b);
        var again = new ZigSettings();
        WorkspaceSettings.ApplyTo(again, a);
        check(other.ZigPath == @"C:\zig-0.15\zig.exe" && other.DefaultFlags == "-O Debug" && again.ZigPath == zig.ZigPath && again.DefaultFlags == zig.DefaultFlags,
            "Two workspaces resolve two independent compilers and flag sets");

        check(ZigLanguageServer.ConfigFolderFor(a) != ZigLanguageServer.ConfigFolderFor(b)
            && ZigLanguageServer.ConfigFolderFor(a) == ZigLanguageServer.ConfigFolderFor(a.ToUpperInvariant() + "\\")
            && Path.GetDirectoryName(ZigLanguageServer.ConfigFolderFor(null)) == ZigLanguageServer.ConfigFolder,
            "Each workspace writes its own zls.json, so two windows never read each other's zig.exe");

        // Rejected values keep the global value and explain why.
        Write(b, @"{
  ""vistudio.zig.defaultFlags"": ""-O\u0001"",
  ""vistudio.zig.buildStep"": ""bad step!"",
  ""vistudio.zig.astCheckOnSave"": ""no"",
  ""vistudio.zig.diagnosticLevel"": ""1"",
  ""vistudio.zig.astCheckDelayMilliseconds"": 5,
  ""vistudio.zig.noSuchSetting"": 1,
  ""vistudio.kotlin.buildTasks"": ""build --offline"",
  ""vistudio.python.path"": ""x""
}");
        var guarded = new ZigSettings { DefaultFlags = "-keep", BuildStep = "test" };
        warnings = WorkspaceSettings.ApplyTo(guarded, b);
        check(guarded.DefaultFlags == "-keep" && guarded.BuildStep == "test" && guarded.AstCheckOnSave && guarded.DiagnosticLevel == ZigDiagnosticLevel.ErrorsAndNotes && guarded.AstCheckDelayMilliseconds == 600,
            "Invalid flags, build step, types, enum numbers and too small delays are ignored");
        check(warnings.Count == 6 && warnings.Any(w => w.StartsWith("vistudio.zig.noSuchSetting: unknown setting", StringComparison.Ordinal)),
            "Every ignored Zig key is reported: " + string.Join("; ", warnings));
        var guardedKotlin = new KotlinSettings();
        WorkspaceSettings.ApplyTo(guardedKotlin, b);
        check(guardedKotlin.BuildTasks == "build", "Gradle task lists are validated like Tools > Options does");
        var all = WorkspaceSettings.Check(b);
        check(all.Count == 8 && all.Any(w => w.StartsWith("vistudio.python.path: unknown section", StringComparison.Ordinal)),
            "Check reports file, Kotlin and Zig problems together: " + string.Join("; ", all));

        // The cache notices edits; broken JSON falls back to the global values with one warning.
        Write(b, "{ \"vistudio.zig.zigPath\": ");
        var broken = new ZigSettings { ZigPath = "global" };
        WorkspaceSettings.ApplyTo(broken, b);
        var brokenWarnings = WorkspaceSettings.Check(b);
        check(broken.ZigPath == "global" && brokenWarnings.Count == 1 && brokenWarnings[0].Contains("invalid JSON"),
            "Invalid JSON is reported once and changes nothing");
        Write(b, "[1, 2]");
        check(WorkspaceSettings.Check(b).Single().Contains("one JSON object"), "A non-object root is rejected");
        Write(b, "");
        check(WorkspaceSettings.Check(b).Count == 0, "An empty file is the same as no file");

        // Precedence: global < .kotlinproj < .vs\settings.json.
        var project = Path.Combine(root, "precedence");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "gradlew.bat"), "@echo off\r\n");
        var metadata = KotlinProjectFile.Create(project, new KotlinSettings());
        metadata.Set("BuildTasks", ":app:build");
        metadata.Set("RunTasks", ":app:run");
        metadata.Save();
        Write(project, "{ \"vistudio.kotlin.buildTasks\": \":app:assemble\" }");
        var layered = new KotlinSettings();
        KotlinSettings.ApplyProject(project, layered);
        WorkspaceSettings.ApplyTo(layered, project);
        check(layered.BuildTasks == ":app:assemble" && layered.RunTasks == ":app:run" && layered.TestTasks == "test",
            "Workspace file overrides the project file, which overrides the global value");

        // The window's root: nested folders map to the open workspace, unrelated folders to themselves.
        var previous = WorkspaceSettings.CurrentRoot;
        try
        {
            WorkspaceSettings.CurrentRoot = a + "\\";
            check(WorkspaceSettings.CurrentRoot == a, "The current root is normalized");
            check(WorkspaceSettings.RootFor(Path.Combine(a, "app", "src")) == a && WorkspaceSettings.RootFor(b) == b && WorkspaceSettings.RootFor(null) == a,
                "Folders inside the window's workspace use its settings file; others use their own");
            check(WorkspaceSettings.RootFor(a + "-suffix") == a + "-suffix", "A sibling folder with the same prefix is not inside the workspace");
            WorkspaceSettings.CurrentRoot = @"C:\";
            check(WorkspaceSettings.CurrentRoot == @"C:\" && WorkspaceSettings.RootFor(@"C:\work") == @"C:\", "A drive root stays a drive root");
            WorkspaceSettings.CurrentRoot = null;
            check(WorkspaceSettings.RootFor(b) == b, "Without a window workspace the folder's own file is used");
        }
        finally { WorkspaceSettings.CurrentRoot = previous; }
    }
}
