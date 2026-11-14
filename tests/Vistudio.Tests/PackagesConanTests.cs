using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vistudio.Core.Packages;

internal static class PackagesConanTests
{
    private const string Sample =
        "# deps\n[requires]\nfmt/9.1.0\nzlib/[>=1.2 <1.3]\nboost/1.84.0@user/stable#abc123\n\n[tool_requires]\ncmake/3.28.1\n[test_requires]\ngtest/1.14.0\n[generators]\nCMakeDeps\n";

    internal static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "conan-unit");
        Directory.CreateDirectory(folder);
        var manager = new ConanPackageManager();
        check(manager.Ecosystem == PackageEcosystem.Conan && manager.ManifestNames.SequenceEqual(new[] { "conanfile.txt", "conanfile.py" }) && manager.ToolNames.Contains("conan"), "conan identity");
        Reading(manager, folder, check);
        Python(manager, folder, check);
        Outdated(manager, folder, check);
        Edits(manager, folder, check);
        Commands(manager, folder, check);
    }

    private static string Write(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Reading(ConanPackageManager manager, string folder, Action<bool, string> check)
    {
        var path = Write(folder, "conanfile.txt", Sample);
        var entries = manager.ReadManifest(path);
        check(entries.Select(e => e.Name).SequenceEqual(new[] { "fmt", "zlib", "boost", "cmake", "gtest" }), "txt names: " + string.Join(",", entries.Select(e => e.Name)));
        check(entries.Select(e => e.Line).SequenceEqual(new[] { 3, 4, 5, 8, 10 }), "txt lines: " + string.Join(",", entries.Select(e => e.Line)));
        check(entries.Select(e => e.Requested).SequenceEqual(new[] { "9.1.0", "[>=1.2 <1.3]", "1.84.0", "3.28.1", "1.14.0" }), "txt requested: " + string.Join("|", entries.Select(e => e.Requested)));
        check(entries[0].Scope == PackageScope.Runtime && entries[3].Scope == PackageScope.Build && entries[4].Scope == PackageScope.Development, "txt scopes");
        check(entries[0].Group == "[requires]" && entries[3].Group == "[tool_requires]" && entries.All(e => e.Current == null), "txt groups, no lock, no current");

        Write(folder, "conan.lock", "{\"version\":\"0.5\",\"requires\":[\"fmt/9.1.0#rev%123\",\"zlib/1.2.13#r%1\",\"zlib/1.2.11#r%1\"],\"build_requires\":[\"cmake/3.28.1#x%1\"],\"python_requires\":[]}");
        var locked = manager.ReadManifest(path);
        check(locked[0].Current == "9.1.0" && locked[1].Current == "1.2.13" && locked[3].Current == "3.28.1" && locked[2].Current == null, "conan.lock gives Current: " + string.Join(",", locked.Select(e => e.Current ?? "-")));
        File.WriteAllText(Path.Combine(folder, "conan.lock"), "{ not json");
        check(manager.ReadManifest(path).All(e => e.Current == null), "unreadable lock file is ignored");
        File.Delete(Path.Combine(folder, "conan.lock"));

        check(manager.ReadManifest(Write(folder, "conanfile.txt", "")).Count == 0, "empty conanfile.txt");
        var missing = false;
        try { manager.ReadManifest(Path.Combine(folder, "no-such", "conanfile.txt")); } catch (PackageManagerException) { missing = true; }
        check(missing, "missing manifest is a PackageManagerException");
    }

    private static void Python(ConanPackageManager manager, string folder, Action<bool, string> check)
    {
        var py = string.Join("\n", new[]
        {
            "from conan import ConanFile",
            "class P(ConanFile):",
            "    requires = \"a/1.0\", \"b/2.0#rev\"",
            "    tool_requires = (\"cmake/[>=3.20 <4]\",)",
            "    # requires = \"commented/1.0\"",
            "    test_requires = 'gt/1.0'  # trailing \"note/9.9\"",
            "    requires = [",
            "        \"d/1.0\",",
            "        \"e/2.0\",",
            "    ]",
            "    def requirements(self):",
            "        self.requires(\"c/3.0\", transitive_headers=True)",
            "        self.tool_requires('ninja/1.11.1')",
            "        # self.requires(\"nope/1.0\")",
            ""
        });
        var path = Write(folder, "conanfile.py", py);
        var entries = manager.ReadManifest(path);
        check(entries.Select(e => e.Name).SequenceEqual(new[] { "a", "b", "cmake", "gt", "d", "e", "c", "ninja" }), "py names: " + string.Join(",", entries.Select(e => e.Name)));
        check(entries.Select(e => e.Requested).SequenceEqual(new[] { "1.0", "2.0", "[>=3.20 <4]", "1.0", "1.0", "2.0", "3.0", "1.11.1" }), "py versions: " + string.Join("|", entries.Select(e => e.Requested)));
        check(entries[2].Scope == PackageScope.Build && entries[3].Scope == PackageScope.Development && entries[6].Scope == PackageScope.Runtime && entries[7].Scope == PackageScope.Build, "py scopes");
        check(entries[0].Line == 3 && entries[6].Line == 12, "py lines: " + entries[0].Line + "," + entries[6].Line);

        foreach (var action in new Func<PackageAction>[]
        {
            () => manager.Add("conan.exe", path, new PackageRequest("fmt", "1.0")),
            () => manager.Remove("conan.exe", path, entries[0]),
            () => manager.Update("conan.exe", path, entries[0].WithVersions("1.0", null, "2.0"), true),
        })
        {
            string? message = null;
            try { action(); } catch (PackageManagerException e) { message = e.Message; }
            check(message != null && message.Contains("by hand"), "python conanfile is not edited: " + message);
        }
        var inside = manager.Update("conan.exe", path, entries[0], false);
        check(inside.Edits.Count == 0 && inside.Commands.Count == 1 && inside.Commands[0].Arguments.Contains("--update=a"), "python conanfile can still update inside constraints");
    }

    private const string OutdatedJson =
        "{\"fmt\": {\"current_versions\": [\"fmt/9.1.0\"], \"version_ranges\": [], \"latest_remote\": {\"ref\": \"fmt/12.2.0\", \"remote\": \"conancenter\"}}, " +
        "\"zlib\": {\"current_versions\": [\"zlib/1.2.13\"], \"version_ranges\": [\"zlib/[>=1.2 <1.3]\"], \"latest_remote\": {\"ref\": \"zlib/1.4\", \"remote\": \"conancenter\"}}, " +
        "\"cmake\": {\"current_versions\": [\"cmake/3.28.1\"], \"version_ranges\": [], \"latest_remote\": null}, " +
        "\"transitive\": {\"current_versions\": [\"transitive/1.0\"], \"version_ranges\": [], \"latest_remote\": {\"ref\": \"transitive/2.0\", \"remote\": \"conancenter\"}}}";

    private static void Outdated(ConanPackageManager manager, string folder, Action<bool, string> check)
    {
        var path = Write(folder, "conanfile.txt", Sample);
        var declared = manager.ReadManifest(path);
        var commands = manager.CreateOutdated("C:\\tools\\conan.exe", path, declared);
        check(commands.Count == 1 && commands[0].FileName == "C:\\tools\\conan.exe" && commands[0].Arguments == PackageArguments.Join(new[] { "graph", "outdated", folder, "--format=json" })
            && commands[0].WorkingDirectory == folder, "outdated command: " + (commands.Count == 1 ? commands[0].Arguments : "none"));
        check(manager.CreateOutdated("conan.exe", path, new PackageEntry[0]).Count == 0, "nothing declared, nothing asked");

        var merged = manager.ApplyOutdated(declared, new[] { new PackageToolOutput(0, "progress line\n" + OutdatedJson + "\n", "ERROR: warnings only") });
        var fmt = merged[0];
        check(fmt.Current == "9.1.0" && fmt.Compatible == null && fmt.Latest == "12.2.0" && fmt.IsOutdated && !fmt.HasCompatibleUpdate, "fmt pinned: latest only");
        var zlib = merged[1];
        check(zlib.Current == "1.2.13" && zlib.Compatible == "1.2.13" && zlib.Latest == "1.4", "zlib range resolves to compatible: " + zlib.Current + "/" + zlib.Compatible + "/" + zlib.Latest);
        check(merged[3].Current == "3.28.1" && merged[3].Latest == null && !merged[3].IsOutdated, "no latest_remote");
        check(merged[2].Current == "1.84.0" && merged[2].Latest == null && !merged[2].IsOutdated, "pinned package missing from the report is at its pinned version");
        check(merged.Count == declared.Count, "same count");
        check(manager.ApplyOutdated(declared, new PackageToolOutput[0]).Count == declared.Count, "no outputs, unchanged");

        string? failure = null;
        try { manager.ApplyOutdated(declared, new[] { new PackageToolOutput(1, "", "ERROR: Package 'x/1' not resolved: Unable to find 'x/1' in remotes.\n") }); }
        catch (PackageManagerException e) { failure = e.Message; }
        check(failure != null && failure.Contains("Unable to find"), "failed report carries conan's message: " + failure);
        string? broken = null;
        try { manager.ApplyOutdated(declared, new[] { new PackageToolOutput(0, "not json at all", "") }); }
        catch (PackageManagerException e) { broken = e.Message; }
        check(broken != null, "non-JSON output is a PackageManagerException");
    }

    private static void Edits(ConanPackageManager manager, string folder, Action<bool, string> check)
    {
        string Edit(string text, Func<string, PackageAction> action, out PackageAction result)
        {
            var path = Write(folder, "conanfile.txt", text);
            result = action(path);
            check(result.Edits.Count == 1 && result.Edits[0].Path == path && result.Commands.Count == 1, "one edit then one command");
            return result.Edits[0].Text;
        }

        var added = Edit("[requires]\nfmt/9.1.0\n[generators]\nCMakeDeps\n", p => manager.Add("conan.exe", p, new PackageRequest("zlib", "1.3.1")), out var addAction);
        check(added == "[requires]\nfmt/9.1.0\nzlib/1.3.1\n[generators]\nCMakeDeps\n", "add into existing section: " + Show(added));
        check(addAction.Commands[0].Arguments == PackageArguments.Join(new[] { "install", folder, "--build=missing" }) && addAction.Description.Contains("conan profile detect"), "add runs install and mentions the profile");

        var build = Edit("[requires]\r\nfmt/9.1.0", p => manager.Add("conan.exe", p, new PackageRequest("cmake", "[>=3.20 <4]", PackageScope.Build)), out _);
        check(build == "[requires]\r\nfmt/9.1.0\r\n\r\n[tool_requires]\r\ncmake/[>=3.20 <4]\r\n", "add creates tool_requires, CRLF kept: " + Show(build));
        var test = Edit("[requires]\nfmt/9.1.0\n", p => manager.Add("conan.exe", p, new PackageRequest("gtest", "1.14.0", PackageScope.Development)), out _);
        check(test == "[requires]\nfmt/9.1.0\n\n[test_requires]\ngtest/1.14.0\n", "add creates test_requires: " + Show(test));
        var created = Edit("[generators]\nCMakeDeps\n", p => manager.Add("conan.exe", p, new PackageRequest("fmt", "1.0")), out _);
        check(created == "[generators]\nCMakeDeps\n\n[requires]\nfmt/1.0\n", "add creates [requires], order kept: " + Show(created));
        var empty = Edit("", p => manager.Add("conan.exe", p, new PackageRequest("fmt", "1.0")), out _);
        check(empty == "[requires]\r\nfmt/1.0\r\n", "add into an empty file: " + Show(empty));
        var comments = Edit("# top\n[requires]\n# keep me\nfmt/1.0\n# tail note\n\n[options]\nfmt/*:shared=True\n", p => manager.Add("conan.exe", p, new PackageRequest("zlib", "1.3.1")), out _);
        check(comments == "# top\n[requires]\n# keep me\nfmt/1.0\n# tail note\nzlib/1.3.1\n\n[options]\nfmt/*:shared=True\n", "add keeps comments: " + Show(comments));

        var path = Write(folder, "conanfile.txt", Sample);
        var entries = manager.ReadManifest(path);
        var removed = manager.Remove("conan.exe", path, entries[1]).Edits[0].Text;
        check(removed == Sample.Replace("zlib/[>=1.2 <1.3]\n", ""), "remove one line only: " + Show(removed));
        var lastNoEol = Edit("[requires]\nfmt/1\nzlib/2", p => manager.Remove("conan.exe", p, manager.ReadManifest(p)[1]), out _);
        check(lastNoEol == "[requires]\nfmt/1", "remove the final line without newline: " + Show(lastNoEol));

        Write(folder, "conanfile.txt", Sample);
        var toLatest = manager.Update("conan.exe", path, entries[2].WithVersions("1.84.0", null, "1.85.0"), true);
        check(toLatest.Edits[0].Text == Sample.Replace("boost/1.84.0@user/stable#abc123", "boost/1.85.0@user/stable"), "update to latest keeps @user/channel, drops revision: " + Show(toLatest.Edits[0].Text));
        var range = manager.Update("conan.exe", path, entries[1].WithVersions("1.2.13", "1.2.13", "1.3.1"), true);
        check(range.Edits[0].Text == Sample.Replace("zlib/[>=1.2 <1.3]", "zlib/1.3.1") && range.Commands.Count == 1, "update replaces a range with the exact version");

        foreach (var (what, call) in new (string, Func<PackageAction>)[]
        {
            ("latest unknown", () => manager.Update("conan.exe", path, entries[0], true)),
            ("update all to latest", () => manager.Update("conan.exe", path, null, true)),
            ("add without version", () => manager.Add("conan.exe", path, new PackageRequest("zlib"))),
            ("add with source", () => manager.Add("conan.exe", path, new PackageRequest("zlib", "1.0", PackageScope.Runtime, "https://example.com/x"))),
            ("add twice", () => manager.Add("conan.exe", path, new PackageRequest("fmt", "1.0"))),
            ("add odd name", () => manager.Add("conan.exe", path, new PackageRequest("a/b", "1.0"))),
            ("remove unknown", () => manager.Remove("conan.exe", path, new PackageEntry("nothere", "1", PackageScope.Runtime, "", path, 1))),
        })
        {
            var rejected = false;
            try { call(); } catch (PackageManagerException) { rejected = true; }
            check(rejected, "PackageManagerException: " + what);
        }
        var hostile = false;
        try { new PackageRequest("fmt", "1.0 & calc"); } catch (ArgumentException) { hostile = true; }
        check(hostile, "version with shell metacharacter is rejected before it reaches Conan");
    }

    private static void Commands(ConanPackageManager manager, string folder, Action<bool, string> check)
    {
        var path = Write(folder, "conanfile.txt", Sample);
        var entry = manager.ReadManifest(path)[0];
        var all = manager.Update("conan.exe", path, null, false);
        check(all.Edits.Count == 0 && all.Commands.Count == 1 && all.Commands[0].Arguments == PackageArguments.Join(new[] { "install", folder, "--update", "--build=missing" }), "update all inside constraints: " + all.Commands[0].Arguments);
        var one = manager.Update("conan.exe", path, entry, false);
        check(one.Commands[0].Arguments == PackageArguments.Join(new[] { "install", folder, "--update=fmt", "--build=missing" }) && one.Commands[0].WorkingDirectory == folder, "update one inside constraints: " + one.Commands[0].Arguments);
        check(all.Commands[0].RedirectStandardOutput && !all.Commands[0].UseShellExecute, "commands are redirected, no shell");
    }

    private static string Show(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    // ---- live ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Real Conan on a conanfile.txt in a temp folder. CONAN_HOME points under <paramref name="root"/> so the user's Conan
    /// configuration, profiles and package cache are never touched; <c>conan profile detect</c> runs there. No install, no builds.
    /// </summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var conan = FindConan();
        if (conan == null) { Console.WriteLine("SKIP: conan not found; live conan checks skipped."); return false; }
        var live = Path.Combine(root, "conan-live");
        var home = Path.Combine(live, "home");
        var project = Path.Combine(live, "project");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(project);
        var environment = new Dictionary<string, string> { ["CONAN_HOME"] = home };

        var version = await RunAsync(Start(conan, live, environment, "--version"));
        check(version.ExitCode == 0 && version.StandardOutput.Contains("Conan version 2."), "live: Conan 2 installed: " + version.StandardOutput.Trim());
        var detect = await RunAsync(Start(conan, live, environment, "profile", "detect"));
        check(detect.ExitCode == 0, "live: profile detect in the private CONAN_HOME: " + detect.StandardError.Trim());

        var manifest = Path.Combine(project, "conanfile.txt");
        File.WriteAllText(manifest, "[requires]\nfmt/9.1.0\nzlib/[>=1.2 <1.3]\n\n[generators]\nCMakeDeps\n");
        var manager = new ConanPackageManager();
        var declared = manager.ReadManifest(manifest);
        check(declared.Count == 2 && declared[0].Requested == "9.1.0", "live: manifest read");

        var merged = await OutdatedAsync(manager, conan, manifest, declared, environment);
        var fmt = merged.First(e => e.Name == "fmt");
        var zlib = merged.First(e => e.Name == "zlib");
        check(fmt.Current == "9.1.0" && fmt.Latest != null && fmt.IsOutdated, "live: fmt 9.1.0 is flagged outdated, latest " + fmt.Latest);
        check(zlib.Current != null && zlib.Compatible == zlib.Current && zlib.Latest != null && PackageVersion.Compare(zlib.Latest, "1.3") >= 0, "live: zlib range resolved " + zlib.Current + ", latest " + zlib.Latest);
        Console.WriteLine("conan live: fmt " + fmt.Caption + "; zlib " + zlib.Caption + " (conan " + version.StandardOutput.Trim() + ")");

        var action = manager.Update(conan, manifest, fmt, true);
        check(action.Edits.Count == 1 && action.Commands.Count == 1, "live: update to latest is an edit plus install");
        File.WriteAllText(action.Edits[0].Path, action.Edits[0].Text);
        var reread = manager.ReadManifest(manifest);
        check(reread[0].Name == "fmt" && reread[0].Requested == fmt.Latest && reread[1].Requested == "[>=1.2 <1.3]", "live: re-read shows fmt at " + reread[0].Requested);
        check(File.ReadAllText(manifest).Contains("[generators]\nCMakeDeps"), "live: the rest of the file is untouched");

        var again = await OutdatedAsync(manager, conan, manifest, reread, environment);
        var fmtNow = again.First(e => e.Name == "fmt");
        check(fmtNow.Current == fmt.Latest && fmtNow.Latest == null && !fmtNow.IsOutdated, "live: after the edit fmt is current: " + fmtNow.Current + "/" + fmtNow.Latest + "/" + fmtNow.Compatible);
        return true;
    }

    private static async Task<IReadOnlyList<PackageEntry>> OutdatedAsync(ConanPackageManager manager, string conan, string manifest, IReadOnlyList<PackageEntry> declared, Dictionary<string, string> environment)
    {
        var commands = manager.CreateOutdated(conan, manifest, declared);
        var outputs = new List<PackageToolOutput>();
        foreach (var command in commands)
        {
            foreach (var pair in environment) command.EnvironmentVariables[pair.Key] = pair.Value;
            outputs.Add(await RunAsync(command));
        }
        return manager.ApplyOutdated(declared, outputs);
    }

    private static string? FindConan()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var candidate = Path.Combine(folder.Trim().Trim('"'), "conan.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static ProcessStartInfo Start(string exe, string folder, Dictionary<string, string> environment, params string[] arguments)
    {
        var start = Vistudio.Core.ProcessCommands.CreateRedirected(exe, PackageArguments.Join(arguments), folder);
        foreach (var pair in environment) start.EnvironmentVariables[pair.Key] = pair.Value;
        return start;
    }

    private static async Task<PackageToolOutput> RunAsync(ProcessStartInfo start)
    {
        using (var process = Process.Start(start)!)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(150000));
            if (!exited) { try { process.Kill(); } catch (InvalidOperationException) { } return new PackageToolOutput(-1, "", "timed out"); }
            return new PackageToolOutput(process.ExitCode, await output, await error);
        }
    }
}
