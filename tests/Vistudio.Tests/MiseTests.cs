using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vistudio.Core.Mise;

internal static class MiseTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Files(Path.Combine(root, "mise-files"), check);
        Locator(check);
        Report(check);
        Commands(check);
        Settings(Path.Combine(root, "mise-settings"), check);
    }

    private static void Files(string root, Action<bool, string> check)
    {
        foreach (var name in new[] { "mise.toml", ".mise.toml", "mise.local.toml", ".mise.local.toml", "mise.dev.toml", "mise.dev.local.toml", ".tool-versions" })
            check(MiseConfigFiles.IsConfigFile(Path.Combine(root, name)), "mise config name: " + name);
        check(MiseConfigFiles.IsConfigFile(Path.Combine(root, ".config", "mise.toml")), ".config/mise.toml");
        check(MiseConfigFiles.IsConfigFile(Path.Combine(root, ".config", "mise", "config.toml")), ".config/mise/config.toml");
        check(MiseConfigFiles.IsConfigFile(Path.Combine(root, "mise", "config.local.toml")), "mise/config.local.toml");
        check(MiseConfigFiles.IsConfigFile(Path.Combine(root, ".config", "mise", "conf.d", "tools.toml")), "conf.d/*.toml");
        foreach (var name in new[] { "config.toml", "Cargo.toml", "mise.json", "promise.toml", "tool-versions" })
            check(!MiseConfigFiles.IsConfigFile(Path.Combine(root, name)), "not a mise config: " + name);
        check(!MiseConfigFiles.IsConfigFile(null) && !MiseConfigFiles.IsConfigFile(""), "empty path");

        check(MiseConfigFiles.ProjectFolder(Path.Combine(root, "mise.toml")) == root, "project folder of mise.toml");
        check(MiseConfigFiles.ProjectFolder(Path.Combine(root, ".config", "mise", "config.toml")) == root, "project folder of .config/mise/config.toml");
        check(MiseConfigFiles.ProjectFolder(Path.Combine(root, ".config", "mise", "conf.d", "a.toml")) == root, "project folder of conf.d");
        check(MiseConfigFiles.ProjectFolder(Path.Combine(root, ".config", "mise.toml")) == root, "project folder of .config/mise.toml");

        Directory.CreateDirectory(Path.Combine(root, ".config", "mise", "conf.d"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        foreach (var file in new[] { "mise.toml", ".tool-versions", "Cargo.toml", Path.Combine(".config", "mise", "config.toml"), Path.Combine(".config", "mise", "conf.d", "x.toml"), Path.Combine("sub", "mise.toml") })
            File.WriteAllText(Path.Combine(root, file), "");
        var found = MiseConfigFiles.Find(root).Select(p => p.Substring(root.Length + 1)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        check(found.SequenceEqual(new[] { ".config\\mise\\conf.d\\x.toml", ".config\\mise\\config.toml", ".tool-versions", "mise.toml" }), "workspace detection: " + string.Join(", ", found));
        check(MiseConfigFiles.Find(Path.Combine(root, "missing")).Count == 0 && MiseConfigFiles.Find(null).Count == 0, "missing folder finds nothing");
        check(MiseConfigFiles.SamePath(@"C:\Users\me\.config/mise/config.toml", @"c:\users\me\.config\mise\config.toml"), "mixed separators compare equal");
    }

    private static void Locator(Action<bool, string> check)
    {
        var toml = string.Join("\r\n", new[]
        {
            "min_version = \"2024.1\"",
            "tools.java = \"21\"          # dotted key before any table",
            "[env]",
            "NODE = \"x\"",
            "[tools]  # tools",
            "node = \"22\"",
            "\"npm:prettier\" = \"3\"",
            "'aqua:BurntSushi/ripgrep' = { version = \"14\", os = [\"windows\"] }",
            "python = [\"3.12\", \"3.11\"]",
            "# zig = \"0.13\"",
            "[tools.go]",
            "version = \"1.23\"",
            "[tasks.build]",
            "run = \"x\"",
            "[[hooks]]",
            "uv = \"1\"",
        });
        var layout = MiseToolLocator.Find(toml, toolVersions: false);
        var names = layout.Tools.Select(t => t.Name).ToArray();
        check(names.SequenceEqual(new[] { "java", "node", "npm:prettier", "aqua:BurntSushi/ripgrep", "python", "go" }), "TOML tools: " + string.Join(", ", names));
        var lines = layout.Tools.Select(t => t.Line).ToArray();
        check(lines.SequenceEqual(new[] { 1, 5, 6, 7, 8, 10 }), "TOML tool lines: " + string.Join(", ", lines));
        foreach (var tool in layout.Tools)
            check(toml.Substring(tool.Start, tool.End - tool.Start) == tool.Name, "offsets of " + tool.Name);
        check(layout.HeaderLine == 1, "header is the first tools key (dotted key comes first): " + layout.HeaderLine);

        var plain = "[tools]\nnode = \"22\"\n";
        var simple = MiseToolLocator.Find(plain, false);
        check(simple.HeaderLine == 0 && plain.Substring(simple.HeaderStart, simple.HeaderEnd - simple.HeaderStart) == "[tools]", "[tools] header range");
        check(MiseToolLocator.Find("[env]\nA = \"1\"\n", false).HeaderLine == null, "no tools, no header");

        var versions = "# comment\n\nnodejs 22.1.0 20.0.0\r\n  python 3.12\n";
        var asdf = MiseToolLocator.Find(versions, toolVersions: true);
        check(asdf.Tools.Select(t => t.Name).SequenceEqual(new[] { "nodejs", "python" }), ".tool-versions names");
        check(asdf.Tools.Select(t => t.Line).SequenceEqual(new[] { 2, 3 }) && asdf.HeaderLine == 2, ".tool-versions lines");
        check(versions.Substring(asdf.Tools[1].Start, asdf.Tools[1].End - asdf.Tools[1].Start) == "python", ".tool-versions offsets");

        check(MiseToolLocator.SameTool("nodejs", "node") && MiseToolLocator.SameTool("core:node", "node") && MiseToolLocator.SameTool("golang", "go"), "aliases");
        check(!MiseToolLocator.SameTool("aqua:a/rg", "rg") && !MiseToolLocator.SameTool("node", "deno"), "different tools");
    }

    private const string ListJson = @"{
  ""node"": [ { ""version"": ""26.10.0"", ""requested_version"": ""26"", ""source"": { ""type"": ""mise.toml"", ""path"": ""C:\\p\\mise.toml"" }, ""installed"": true, ""active"": true } ],
  ""ninja"": [ { ""version"": ""1.12.1"", ""requested_version"": ""1.12"", ""source"": { ""type"": ""mise.toml"", ""path"": ""C:\\p\\mise.toml"" }, ""installed"": false, ""active"": false } ],
  ""uv"": [ { ""version"": ""0.12.23"", ""requested_version"": ""0.12"", ""source"": { ""type"": ""mise.toml"", ""path"": ""C:\\p\\mise.toml"" }, ""installed"": true, ""active"": true } ],
  ""cmake"": [ { ""version"": ""4.5.0"", ""requested_version"": ""latest"", ""source"": { ""type"": ""mise.toml"", ""path"": ""C:\\Users\\me\\.config/mise/config.toml"" }, ""installed"": true, ""active"": true } ],
  ""python"": [
    { ""version"": ""3.12.1"", ""requested_version"": ""3.12"", ""source"": { ""path"": ""C:\\p\\.tool-versions"" }, ""installed"": true },
    { ""version"": ""3.11.4"", ""requested_version"": ""3.11"", ""source"": { ""path"": ""C:\\p\\.tool-versions"" }, ""installed"": false } ]
}";
    private const string OutdatedJson = @"{
  ""node"": { ""name"": ""node"", ""requested"": ""26"", ""current"": ""26.10.0"", ""bump"": null, ""latest"": ""26.13.1"", ""source"": { ""path"": ""C:\\p\\mise.toml"" } },
  ""ninja"": { ""name"": ""ninja"", ""requested"": ""1.12"", ""current"": null, ""bump"": null, ""latest"": ""1.12.1"", ""release_url"": ""https://example.invalid/ninja"" }
}";
    private const string BumpJson = @"{
  ""node"": { ""name"": ""node"", ""requested"": ""26"", ""current"": ""26.10.0"", ""bump"": ""27"", ""latest"": ""27.0.2"" },
  ""ninja"": { ""name"": ""ninja"", ""requested"": ""1.12"", ""current"": null, ""bump"": ""1.13"", ""latest"": ""1.13.2"" },
  ""uv"": { ""name"": ""uv"", ""requested"": ""0.12"", ""current"": ""0.12.23"", ""bump"": ""0.13"", ""latest"": ""0.13.8"" }
}";

    private static void Report(Action<bool, string> check)
    {
        var report = MiseReport.Parse(ListJson, OutdatedJson, BumpJson);
        check(report.Tools.Count == 5 && report.UpdatesChecked && report.Error == null, "report parsed");
        var node = report.Tools.Single(t => t.Name == "node");
        check(node.State == MiseToolState.UpdateAvailable && node.LatestInRange == "26.13.1" && node.Latest == "27.0.2" && node.Bump == "27", "node: in range and latest");
        check(node.Hint() == "26.10.0 → 26.13.1 · latest 27.0.2", "node hint: " + node.Hint());
        check(node.Tooltip().Contains("mise upgrade --bump") && node.Tooltip().Contains("writes \"27\""), "node tooltip");
        var ninja = report.Tools.Single(t => t.Name == "ninja");
        check(ninja.State == MiseToolState.Missing && ninja.LatestInRange == null && ninja.Latest == "1.13.2", "ninja: missing with latest");
        check(ninja.Hint() == "not installed (1.12.1) · latest 1.13.2" && ninja.ReleaseUrl == "https://example.invalid/ninja", "ninja hint: " + ninja.Hint());
        var uv = report.Tools.Single(t => t.Name == "uv");
        check(uv.State == MiseToolState.UpdateAvailable && uv.LatestInRange == null && uv.Hint() == "0.12.23 · latest 0.13.8", "uv: only a bump update: " + uv.Hint());
        var cmake = report.Tools.Single(t => t.Name == "cmake");
        check(cmake.State == MiseToolState.UpToDate && cmake.Hint() == "4.5.0", "cmake up to date");
        var python = report.Tools.Single(t => t.Name == "python");
        check(python.State == MiseToolState.Missing && python.Version == "3.12.1, 3.11.4" && python.Requested == "3.12, 3.11", "several versions, one missing");

        check(report.ForFile(@"c:\p\MISE.toml").Select(t => t.Name).SequenceEqual(new[] { "node", "ninja", "uv" }), "tools of one file");
        check(report.ForFiles(new[] { @"C:\p\mise.toml", @"C:\p\.tool-versions" }).Count == 4, "project tools exclude the global config");
        check(MiseReport.Match(report.Tools, "nodejs")?.Name == "node" && MiseReport.Match(report.Tools, "deno") == null, "match written names");
        check(MiseReport.Summary(report.ForFile(@"C:\p\mise.toml")) == "1 not installed · 2 updates", "summary: " + MiseReport.Summary(report.ForFile(@"C:\p\mise.toml")));
        check(MiseReport.Summary(new[] { cmake }) == "all 1 tool up to date", "summary up to date");

        var legacy = MiseReport.Parse(ListJson, @"[ { ""name"": ""node"", ""current"": ""26.10.0"", ""latest"": ""26.11.0"" } ]", null, "network down");
        check(legacy.Tools.Single(t => t.Name == "node").LatestInRange == "26.11.0" && legacy.Error!.Contains("network down"), "array output and update error");
        var unchecked_ = MiseReport.Parse(ListJson, null, null);
        check(!unchecked_.UpdatesChecked && unchecked_.Tools.Single(t => t.Name == "node").State == MiseToolState.UpToDate, "without outdated output");

        var untrusted = MiseReport.FromFailure("mise ERROR error parsing config file: C:\\p\\mise.toml\nmise ERROR Config files in C:\\p\\mise.toml are not trusted.\nTrust them with `mise trust`.\nmise ERROR Version: 2026.9.5\n", 1);
        check(untrusted.UntrustedPath == @"C:\p\mise.toml" && untrusted.Tools.Count == 0, "untrusted config recognized");
        var failed = MiseReport.FromFailure("mise ERROR invalid TOML at line 3\nmise ERROR Run with --verbose or MISE_VERBOSE=1 for more information\n", 1);
        check(failed.UntrustedPath == null && failed.Error == "invalid TOML at line 3", "other failure message: " + failed.Error);
    }

    private static void Commands(Action<bool, string> check)
    {
        var dir = Path.GetTempPath();
        check(MiseCommandFactory.List("mise.exe", dir).Arguments == "ls --current --json", "ls arguments");
        check(MiseCommandFactory.Outdated("mise.exe", dir, bump: true).Arguments == "outdated --bump --json", "outdated --bump arguments");
        var upgrade = MiseCommandFactory.Action("mise.exe", dir, MiseAction.UpgradeToLatest, new[] { "node", "aqua:BurntSushi/ripgrep", "npm:@scope/pkg", "node" });
        check(upgrade.Arguments == "upgrade --bump node aqua:BurntSushi/ripgrep npm:@scope/pkg", "upgrade --bump arguments: " + upgrade.Arguments);
        Environment.SetEnvironmentVariable("MISE_YES", "1");
        try
        {
            var install = MiseCommandFactory.Action("mise.exe", dir, MiseAction.Install, new[] { "node" });
            check(install.Arguments == "install node", "install arguments");
            // MISE_YES would let mise trust configs on its own.
            check(install.EnvironmentVariables["NO_COLOR"] == "1" && !install.EnvironmentVariables.ContainsKey("MISE_YES") && install.WorkingDirectory == dir, "environment and folder");
        }
        finally { Environment.SetEnvironmentVariable("MISE_YES", null); }
        check(MiseCommandFactory.Action("mise.exe", dir, MiseAction.Upgrade, new string[0]).Arguments == "upgrade", "upgrade arguments");
        check(MiseCommandFactory.Action("mise.exe", dir, MiseAction.Trust, new string[0], @"C:\My Project\mise.toml").Arguments == "trust \"C:\\My Project\\mise.toml\"", "trust quotes the path");
        foreach (var bad in new[] { "node & calc", "a b", "\"x\"", "-rf", "" })
        {
            var rejected = false;
            try { MiseCommandFactory.ValidateTool(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "tool name rejected: " + bad);
        }
        check(MiseCommandFactory.Quote("a\\b\"c") == "\"a\\b\\\"c\"" && MiseCommandFactory.Quote("x y\\") == "\"x y\\\\\"", "argument quoting");
        check(MiseCommandFactory.FindExecutable(new MiseSettings { MisePath = Path.Combine(dir, "no-such-mise.exe") }) == null, "configured missing mise.exe is not replaced by PATH");
    }

    private static void Settings(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "mise-settings.xml");
        new MiseSettings { MisePath = @"C:\tools\mise.exe", InfoBar = false, ShowUpToDate = false, CacheMinutes = 0 }.Save(path);
        var loaded = MiseSettings.Load(path);
        check(loaded.MisePath == @"C:\tools\mise.exe" && !loaded.InfoBar && !loaded.ShowUpToDate && loaded.InlineHints && loaded.CodeLens && loaded.CacheMinutes == 1, "settings round trip");
        File.WriteAllText(path, "<broken");
        check(MiseSettings.Load(path).InfoBar, "broken settings fall back to defaults");
    }

    /// <summary>Real mise in a temp folder, trusted only through MISE_TRUSTED_CONFIG_PATHS (nothing is written to mise's trust store).</summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var mise = MiseCommandFactory.FindExecutable(new MiseSettings());
        if (mise == null) { Console.WriteLine("SKIP: mise.exe not found; live mise checks skipped."); return false; }
        var project = Path.Combine(root, "mise-live");
        Directory.CreateDirectory(project);
        var config = Path.Combine(project, "mise.toml");
        // [env] makes mise require trust; an old ninja line is enough for versions (only a dry run installs anything).
        File.WriteAllText(config, "[env]\nVISTUDIO_MISE_TEST = \"1\"\n[tools]\nninja = \"1.11\"\n");

        var untrusted = await MiseCommandFactory.CollectAsync(mise, project, CancellationToken.None);
        check(untrusted.UntrustedPath != null && MiseConfigFiles.SamePath(untrusted.UntrustedPath, config), "live: untrusted config reported: " + untrusted.Error);

        var previous = Environment.GetEnvironmentVariable("MISE_TRUSTED_CONFIG_PATHS");
        Environment.SetEnvironmentVariable("MISE_TRUSTED_CONFIG_PATHS", project);
        try
        {
            var report = await MiseCommandFactory.CollectAsync(mise, project, CancellationToken.None);
            var ninja = MiseReport.Match(report.ForFile(config), "ninja");
            check(ninja != null && ninja.Requested == "1.11", "live: ninja from mise.toml: " + (report.Error ?? string.Join(", ", report.Tools.Select(t => t.Name))));
            if (ninja != null)
            {
                check(ninja.Version.StartsWith("1.11", StringComparison.Ordinal), "live: resolved 1.11.x: " + ninja.Version);
                if (report.UpdatesChecked) check(ninja.Latest != null || ninja.State == MiseToolState.UpToDate, "live: latest ninja known: " + ninja.Hint());
                Console.WriteLine("mise live: ninja " + ninja.Hint() + (report.Error != null ? " (" + report.Error + ")" : ""));
            }
            var layout = MiseToolLocator.Find(File.ReadAllText(config), false);
            check(layout.Tools.Count == 1 && MiseReport.Match(report.ForFile(config), layout.Tools[0].Name) != null, "live: located line matches the report");
            // No install, not even --dry-run: mise install records configs trusted through MISE_TRUSTED_CONFIG_PATHS in its trust store.
        }
        finally { Environment.SetEnvironmentVariable("MISE_TRUSTED_CONFIG_PATHS", previous); }
        // Checking must never trust the config by itself.
        var again = await MiseCommandFactory.CollectAsync(mise, project, CancellationToken.None);
        check(again.UntrustedPath != null, "live: config is still untrusted after the checks");
        return true;
    }
}
