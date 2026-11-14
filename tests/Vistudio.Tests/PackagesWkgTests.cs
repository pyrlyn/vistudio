using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vistudio.Core.Packages;

internal static class PackagesWkgTests
{
    // Copied from docs/manifest.md of bytecodealliance/wasm-pkg-tools (main, checked 2026-11-14), "example v1 lockfile".
    private const string DocsLock =
        "version = 1\n\n" +
        "[[packages]]\nname = \"wasi:cli\"\nregistry = \"wasi.dev\"\n\n" +
        "[[packages.versions]]\nrequirement = \"=0.2.0\"\nversion = \"0.2.0\"\ndigest = \"sha256:e7e85458e11caf76554b724ebf4f113259decf0f3b1ee2e2930de096f72114a7\"\n\n" +
        "[[packages]]\nname = \"wasi:clocks\"\nregistry = \"wasi.dev\"\n\n" +
        "[[packages.versions]]\nrequirement = \"=0.2.0\"\nversion = \"0.2.0\"\ndigest = \"sha256:51911098e929732f65d1d84f8dc393299f18a9e8de632d854714f37142efe97b\"\n\n" +
        "[[packages]]\nname = \"wasi:io\"\nregistry = \"wasi.dev\"\n\n" +
        "[[packages.versions]]\nrequirement = \"=0.2.0\"\nversion = \"0.2.0\"\ndigest = \"sha256:c33b1dbf050f64229ff4decbf9a3d3420e0643a86f5f0cea29f81054820020a6\"\n\n" +
        "[[packages]]\nname = \"wasi:random\"\nregistry = \"wasi.dev\"\n\n" +
        "[[packages.versions]]\nrequirement = \"=0.2.0\"\nversion = \"0.2.0\"\ndigest = \"sha256:5d535edc544d06719cf337861b7917c3d565360295e5dc424046dceddb0a0e42\"\n";

    // Reconstructed from the WIT grammar, not copied from a source: unverified against wit-parser.
    private const string World =
        "// import wasi:fake/commented@9.9.9;\r\n" +
        "package example:app@0.1.0;\r\n" +
        "\r\n" +
        "/* import wasi:fake/block@9.9.9;\r\n" +
        "   /* nested */ import wasi:fake/after-nested@9.9.9;\r\n" +
        "*/\r\n" +
        "interface local {\r\n" +
        "    use wasi:io/streams@0.2.0.{input-stream, output-stream};\r\n" +
        "    use example:app/other@0.1.0.{thing};\r\n" +
        "}\r\n" +
        "world app {\r\n" +
        "    import wasi:cli/environment@0.2.0; // trailing comment about wasi:cli/environment@0.2.0\r\n" +
        "    import wasi:io/streams@0.2.0;\r\n" +
        "    import wasi:clocks/wall-clock;\r\n" +
        "    import log: func(message: string);\r\n" +
        "    include wasi:random/imports@0.2.0-rc.1;\r\n" +
        "    export wasi:http/handler@0.3.0;\r\n" +
        "    import local;\r\n" +
        "}\r\n";

    internal static void Run(string root, Action<bool, string> check)
    {
        var manager = new WkgPackageManager();
        check(manager.Ecosystem == PackageEcosystem.Wkg && manager.DisplayName == "wkg", "Ecosystem and name");
        check(manager.ToolNames.SequenceEqual(new[] { "wkg" }), "Tool names");
        check(manager.ManifestNames.SequenceEqual(new[] { "wkg.toml", "world.wit" }), "Manifest names");

        // --- a project made of a world file only ---
        var project = Path.Combine(root, "plain");
        var witDir = Path.Combine(project, "wit");
        Directory.CreateDirectory(Path.Combine(witDir, "deps", "wasi-io"));
        var world = Path.Combine(witDir, "world.wit");
        File.WriteAllText(world, World);
        // Fetched dependencies live in deps/ and must never be listed as declared.
        File.WriteAllText(Path.Combine(witDir, "deps", "wasi-io", "streams.wit"), "package wasi:io@0.2.0;\ninterface x { use wasi:other/dep@1.0.0.{a}; }\n");

        var entries = manager.ReadManifest(world);
        var names = entries.Select(e => e.Name).ToList();
        check(names.SequenceEqual(new[] { "wasi:io", "wasi:cli", "wasi:clocks", "wasi:random", "wasi:http" }),
            "Foreign packages in order of first reference, comments, own package, deps/ and 'import x: func' excluded: " + string.Join(",", names));
        var io = entries.FirstOrDefault(e => e.Name == "wasi:io");
        check(io != null && io.Requested == "0.2.0" && io.Group == "use" && io.Line == 8 && io.ManifestPath == world, "use: version, group, first line");
        check(io != null && io.Scope == PackageScope.Runtime && io.Source == null && io.Current == null && io.Latest == null, "No lock, no versions beyond the declared one");
        var cli = entries.First(e => e.Name == "wasi:cli");
        check(cli.Group == "import" && cli.Line == 12, "import line: " + cli.Line);
        var clocks = entries.First(e => e.Name == "wasi:clocks");
        check(clocks.Requested == "" && clocks.Group == "import", "Reference without version has empty Requested");
        var random = entries.First(e => e.Name == "wasi:random");
        check(random.Requested == "0.2.0-rc.1" && random.Group == "include", "Pre-release version and include group: " + random.Requested);
        check(entries.First(e => e.Name == "wasi:http").Group == "export", "export is a dependency too");

        // --- wkg.lock supplies Current ---
        File.WriteAllText(Path.Combine(project, "wkg.lock"), DocsLock);
        entries = manager.ReadManifest(world);
        check(entries.First(e => e.Name == "wasi:io").Current == "0.2.0" && entries.First(e => e.Name == "wasi:cli").Current == "0.2.0", "Current from the docs lock sample");
        check(entries.First(e => e.Name == "wasi:random").Current == null, "Lock for =0.2.0 does not describe @0.2.0-rc.1");
        check(entries.First(e => e.Name == "wasi:clocks").Current == null, "Lock for =0.2.0 does not describe an unversioned reference");
        check(entries.First(e => e.Name == "wasi:http").Current == null, "Package absent from the lock");
        check(entries.First(e => e.Name == "wasi:io").Caption == "wasi:io 0.2.0", "Caption without update information");

        File.WriteAllText(Path.Combine(project, "wkg.lock"),
            "version = 1\n[[packages]]\nname = \"wasi:clocks\"\n[[packages.versions]]\nrequirement = \"*\"\nversion = \"0.2.4\"\ndigest = \"sha256:00\"\n");
        check(manager.ReadManifest(world).First(e => e.Name == "wasi:clocks").Current == "0.2.4", "Requirement '*' matches an unversioned reference");
        File.WriteAllText(Path.Combine(project, "wkg.lock"), "this is [not toml");
        check(manager.ReadManifest(world).Count == 5, "A broken lock file does not hide the dependencies");
        File.WriteAllText(Path.Combine(project, "wkg.lock"), DocsLock);

        // --- wkg.toml as the manifest ---
        // Reconstructed from the table names in docs/manifest.md (overrides, workspace.members); values are mine.
        var toml = Path.Combine(project, "wkg.toml");
        File.WriteAllText(toml, "# local copy\n[overrides]\n\"wasi:io\" = { path = \"../local-io/wit\" } # sibling\n\n[metadata]\ndescription = \"x\"\n");
        entries = manager.ReadManifest(toml);
        check(entries.Count == 5 && entries.First(e => e.Name == "wasi:io").Source == "../local-io/wit", "wkg.toml: same project, override shown as Source");
        check(entries.First(e => e.Name == "wasi:cli").Source == null, "Only overridden packages have a Source");

        // --- workspace ---
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "a", "wit"));
        Directory.CreateDirectory(Path.Combine(ws, "b"));
        File.WriteAllText(Path.Combine(ws, "a", "wit", "a.wit"), "package ws:a@1.0.0;\nworld a { import wasi:cli/stdout@0.2.0; }\n");
        File.WriteAllText(Path.Combine(ws, "b", "b.wit"), "package ws:b@1.0.0;\nworld b { import wasi:cli/stdout@0.2.1; import ws:a/x@1.0.0; }\n");
        var wsToml = Path.Combine(ws, "wkg.toml");
        File.WriteAllText(wsToml, "[workspace]\nmembers = [\n  \"a/wit\", # first\n  \"b\",\n]\n");
        // ws:a belongs to the workspace, but it is only excluded when a member declares it as its own package.
        entries = manager.ReadManifest(wsToml);
        check(entries.Select(e => e.Name).SequenceEqual(new[] { "wasi:cli", "ws:a" }) && entries[0].Requested == "0.2.0" && entries[0].ManifestPath.EndsWith("a.wit"),
            "Workspace members are read in listed order, one entry per package: " + string.Join(",", entries.Select(e => e.Name)));
        var wsUpdate = manager.Update(@"C:\tools\wkg.exe", wsToml, null, false);
        check(wsUpdate.Commands[0].Arguments == "wit fetch" && wsUpdate.Commands[0].WorkingDirectory == ws, "Workspace fetch takes no directory: " + wsUpdate.Commands[0].Arguments);

        File.WriteAllText(wsToml, "[workspace]\nmembers = [\"crates/*\"]\n");
        try { manager.ReadManifest(wsToml); check(false, "Glob member accepted"); }
        catch (PackageManagerException ex) { check(ex.Message.Contains("crates/*"), "Glob member refused with its text"); }

        try { manager.ReadManifest(Path.Combine(root, "missing", "wkg.toml")); check(false, "Missing manifest accepted"); }
        catch (PackageManagerException) { check(true, ""); }

        // --- outdated: wkg has nothing to ask ---
        check(manager.CreateOutdated(@"C:\tools\wkg.exe", world, entries).Count == 0, "No outdated command");
        var applied = manager.ApplyOutdated(entries, new PackageToolOutput[0]);
        check(applied.Count == entries.Count && applied.All(e => e.Latest == null && e.Compatible == null), "Outdated report leaves versions null");

        // --- add / remove ---
        try { manager.Add(@"C:\tools\wkg.exe", world, new PackageRequest("wasi:http", "0.2.0")); check(false, "Add accepted"); }
        catch (PackageManagerException ex) { check(ex.Message.Contains("import wasi:http/<interface>@0.2.0;"), "Add tells what to write: " + ex.Message); }
        try { manager.Remove(@"C:\tools\wkg.exe", world, io!); check(false, "Remove accepted"); }
        catch (PackageManagerException ex) { check(ex.Message.Contains("wasi:io"), "Remove says which package to delete by hand"); }

        // --- update ---
        entries = manager.ReadManifest(world);
        var plain = manager.Update(@"C:\tools\wkg.exe", world, entries.First(e => e.Name == "wasi:io"), false);
        check(plain.Edits.Count == 0 && plain.Commands.Count == 1, "Update inside constraints edits nothing");
        check(plain.Commands[0].FileName == @"C:\tools\wkg.exe" && plain.Commands[0].Arguments == "wit fetch" && plain.Commands[0].WorkingDirectory == project,
            "wit fetch in the project folder: " + plain.Commands[0].Arguments + " @ " + plain.Commands[0].WorkingDirectory);
        check(plain.Description.Contains("no command that raises versions"), "Description says wkg cannot raise versions");

        var latest = entries.First(e => e.Name == "wasi:io").WithVersions("0.2.0", null, "0.2.3");
        var bump = manager.Update(@"C:\tools\wkg.exe", world, latest, true);
        check(bump.Edits.Count == 1 && bump.Edits[0].Path == world, "One edit for the one file that mentions the package");
        var expected = World.Replace("wasi:io/streams@0.2.0.{input-stream", "wasi:io/streams@0.2.3.{input-stream").Replace("import wasi:io/streams@0.2.0;", "import wasi:io/streams@0.2.3;");
        check(bump.Edits[0].Text == expected, "Both references rewritten, rest (CRLF, comments, other packages) byte-for-byte");
        check(bump.Edits[0].Text.Contains("// import wasi:fake/commented@9.9.9;") && bump.Edits[0].Text.Contains("wasi:cli/environment@0.2.0; // trailing comment about wasi:cli/environment@0.2.0"), "Comments untouched");
        check(bump.Commands.Count == 1 && bump.Commands[0].Arguments == "wit fetch", "Fetch follows the edit");

        var noVersion = manager.Update(@"C:\tools\wkg.exe", world, entries.First(e => e.Name == "wasi:clocks").WithVersions(null, null, "0.2.4"), true);
        check(noVersion.Edits.Count == 0, "A reference without a version is not given one");

        try { manager.Update(@"C:\tools\wkg.exe", world, entries.First(e => e.Name == "wasi:io"), true); check(false, "Update to unknown latest accepted"); }
        catch (PackageManagerException ex) { check(ex.Message.Contains("newest version"), "Unknown latest explained"); }
        foreach (var bad in new[] { "^1.0", "1.0", "1.2.3 <2" , "1.0.0-" })
        {
            try { manager.Update(@"C:\tools\wkg.exe", world, entries.First(e => e.Name == "wasi:io").WithVersions("0.2.0", null, bad), true); check(false, "Version written into WIT accepted: " + bad); }
            catch (PackageManagerException) { check(true, ""); }
            catch (ArgumentException) { check(true, ""); }
        }

        // --- a WIT folder that is not called 'wit' needs the directory argument ---
        var odd = Path.Combine(root, "odd");
        Directory.CreateDirectory(Path.Combine(odd, "interfaces"));
        File.WriteAllText(Path.Combine(odd, "wkg.toml"), "[metadata]\ndescription = \"x\"\n");
        check(manager.ReadManifest(Path.Combine(odd, "wkg.toml")).Count == 0, "wkg.toml without a wit folder declares nothing");
        var oddWorld = Path.Combine(odd, "interfaces", "w.wit");
        File.WriteAllText(oddWorld, "package a:b;\nworld w { import wasi:cli/stdin@0.2.0; }\n");
        var oddFetch = manager.Update(@"C:\tools\wkg.exe", oddWorld, null, false);
        check(oddFetch.Commands[0].WorkingDirectory == odd + "\\interfaces" || oddFetch.Commands[0].WorkingDirectory == Path.Combine(odd, "interfaces"),
            "A world file outside 'wit' makes its own folder the project: " + oddFetch.Commands[0].WorkingDirectory);
    }

    /// <summary>
    /// Runs the real wkg against a one-import project. Returns false when wkg is not installed. Needs network access to
    /// the default registry; written without wkg available, so the expectations are unverified against a real run.
    /// </summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var wkg = Find();
        if (wkg == null) return false;

        var version = await RunAsync(new ProcessStartInfo(wkg, "--version") { WorkingDirectory = root }, 30000);
        check(version.ExitCode == 0 && version.StandardOutput.StartsWith("wkg", StringComparison.OrdinalIgnoreCase), "wkg --version: " + version.StandardOutput.Trim() + version.StandardError);

        var project = Path.Combine(root, "live");
        Directory.CreateDirectory(Path.Combine(project, "wit"));
        var world = Path.Combine(project, "wit", "world.wit");
        File.WriteAllText(world, "package vistudio:live@0.1.0;\n\nworld live {\n    import wasi:io/error@0.2.0;\n}\n");

        var manager = new WkgPackageManager();
        var declared = manager.ReadManifest(world);
        check(declared.Count == 1 && declared[0].Name == "wasi:io" && declared[0].Current == null, "Live: one declared package, no lock yet");

        var action = manager.Update(wkg, world, null, false);
        var fetch = await RunAsync(action.Commands[0], 180000);
        check(fetch.ExitCode == 0, "Live: wkg wit fetch exit " + fetch.ExitCode + ": " + fetch.StandardError);
        check(File.Exists(Path.Combine(project, "wkg.lock")), "Live: wkg.lock written next to the wit folder");
        check(Directory.Exists(Path.Combine(project, "wit", "deps")), "Live: dependencies fetched into wit/deps");
        declared = manager.ReadManifest(world);
        check(declared.Count == 1 && declared[0].Current == "0.2.0", "Live: Current read from the real wkg.lock: " + (declared.Count == 1 ? declared[0].Current : "?"));
        return true;
    }

    private static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        dirs.Add(Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".cargo", "bin"));
        foreach (var dir in dirs.Where(d => d.Trim().Length != 0))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "wkg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<PackageToolOutput> RunAsync(ProcessStartInfo start, int timeoutMilliseconds)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(timeoutMilliseconds));
            if (!exited)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new PackageToolOutput(-1, "", "timed out");
            }
            return new PackageToolOutput(process.ExitCode, await output, await error);
        }
    }
}
