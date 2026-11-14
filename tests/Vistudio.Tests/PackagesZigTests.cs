using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vistudio.Core.Packages;

internal static class PackagesZigTests
{
    private const string Hash = "mime-3.0.0-zwmL-6wgAADuFwn7gr-_DAQDGJdIim94aDIPa6qO-6GT";

    private static readonly string Manifest = string.Join("\n", new[]
    {
        ".{",
        "    .name = .app,",
        "    .version = \"0.1.0\",",
        "    // .dependencies = .{ .fake = .{ .url = \"https://example.com/fake.tar.gz\" } },",
        "    .dependencies = .{",
        "        .mime = .{",
        "            .url = \"https://github.com/andrewrk/mime/archive/refs/tags/v3.0.0.tar.gz\",",
        "            .hash = \"" + Hash + "\", // pinned",
        "        },",
        "        .@\"quoted-name\" = .{ .path = \"../local\", .lazy = true },",
        "        .pinned = .{",
        "            .url = \"https://github.com/o/pinned/archive/0123456789abcdef0123456789abcdef01234567.tar.gz\",",
        "            .hash = \"1220aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",",
        "        },",
        "        .viagit = .{ .url = \"git+https://codeberg.org/o/lib.git#v2.1.0\" },",
        "        .branch = .{ .url = \"https://github.com/o/br/archive/refs/heads/main.tar.gz\" },",
        "        .gitlab = .{ .url = \"https://gitlab.com/g/s/r/-/archive/1.4.0/r-1.4.0.tar.gz\" },",
        "        .other = .{ .url = \"https://example.com/other-1.0.tar.gz\", .hash = \"x\\\"y\" },",
        "    },",
        "    .paths = .{ \"build.zig\", \"src\" },",
        "}",
        ""
    });

    internal static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "zig-unit");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "build.zig.zon");
        File.WriteAllText(path, Manifest);
        var manager = new ZigPackageManager();
        check(manager.Ecosystem == PackageEcosystem.Zig && manager.ManifestNames.SequenceEqual(new[] { "build.zig.zon" }), "zig identity");

        var entries = manager.ReadManifest(path);
        check(entries.Select(e => e.Name).SequenceEqual(new[] { "mime", "quoted-name", "pinned", "viagit", "branch", "gitlab", "other" }), "names: " + string.Join(",", entries.Select(e => e.Name)));
        check(entries.Select(e => e.Line).SequenceEqual(new[] { 6, 10, 11, 15, 16, 17, 18 }), "lines: " + string.Join(",", entries.Select(e => e.Line)));
        var mime = entries[0];
        check(mime.Requested == "v3.0.0" && mime.Current == "v3.0.0" && mime.Source!.EndsWith("v3.0.0.tar.gz"), "tag url: " + mime.Requested + "/" + mime.Current);
        check(entries[1].Source == "../local" && entries[1].Requested == "" && entries[1].Current == null, "path dependency");
        check(entries[2].Requested == "0123456789abcdef0123456789abcdef01234567" && entries[2].Current == null, "commit pinned: no version");
        check(entries[3].Requested == "v2.1.0" && entries[3].Current == "v2.1.0", "git+ fragment tag");
        check(entries[4].Requested == "main" && entries[4].Current == null, "branch archive");
        check(entries[5].Requested == "1.4.0", "gitlab archive tag");
        check(entries[6].Requested == "" && entries[6].Source == "https://example.com/other-1.0.tar.gz", "unknown host: no version");
        check(ZigPackageManager.HashVersion(Hash) == "3.0.0" && ZigPackageManager.HashVersion("1220" + new string('a', 64)) == null
            && ZigPackageManager.HashVersion("pkg-1.0.0-rc.1-" + new string('A', 44)) == "1.0.0-rc.1", "hash version");
        check(manager.ReadManifest(WriteFile(folder, "b.zon", ".{ .name = .x }")).Count == 0 && manager.ReadManifest(WriteFile(folder, "c.zon", ".{ .dependencies = .{} }")).Count == 0, "no dependencies");
        string? bad = null;
        try { manager.ReadManifest(WriteFile(folder, "d.zon", "nonsense")); } catch (PackageManagerException e) { bad = e.Message; }
        check(bad != null, "not ZON is a PackageManagerException");

        Outdated(manager, folder, path, entries, check);
        Edits(manager, folder, path, entries, check);
    }

    private static string WriteFile(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Outdated(ZigPackageManager manager, string folder, string path, IReadOnlyList<PackageEntry> entries, Action<bool, string> check)
    {
        var git = ZigPackageManager.FindGit();
        var commands = manager.CreateOutdated("zig.exe", path, entries);
        if (git == null) check(commands.Count == 0, "no git, no commands");
        else
        {
            // Only dependencies pinned to a tag on a git host are asked: mime, viagit, gitlab.
            check(commands.Count == 3, "three git commands: " + commands.Count);
            if (commands.Count == 3)
            {
                check(commands[0].FileName == git && commands[0].Arguments == "ls-remote --tags --refs https://github.com/andrewrk/mime", "mime command: " + commands[0].Arguments);
                check(commands[1].Arguments == "ls-remote --tags --refs https://codeberg.org/o/lib.git" && commands[2].Arguments == "ls-remote --tags --refs https://gitlab.com/g/s/r", "other commands: " + commands[1].Arguments + " | " + commands[2].Arguments);
                check(commands[0].WorkingDirectory == folder && commands[0].EnvironmentVariables["GIT_TERMINAL_PROMPT"] == "0", "folder and no password prompt");
            }
        }

        var sha = new string('a', 40);
        var outputs = new[]
        {
            new PackageToolOutput(0, sha + "\trefs/tags/v2.9.0\n" + sha + "\trefs/tags/v3.1.0\n" + sha + "\trefs/tags/v3.2.0-rc.1\n" + sha + "\trefs/tags/nightly\n" + sha + "\trefs/tags/v3.0.0\n", ""),
            new PackageToolOutput(128, "", "fatal: repository not found"),
            new PackageToolOutput(0, sha + "\trefs/tags/1.4.0\n" + sha + "\trefs/tags/1.10.0\n", ""),
        };
        var merged = manager.ApplyOutdated(entries, outputs);
        check(merged.Count == entries.Count && merged.Select(e => e.Name).SequenceEqual(entries.Select(e => e.Name)), "same order and count");
        check(merged[0].Latest == "v3.1.0" && merged[0].Compatible == "v3.1.0" && merged[0].Current == "v3.0.0" && merged[0].IsOutdated, "mime: newest stable tag: " + merged[0].Latest);
        check(merged[3].Latest == null && !merged[3].IsOutdated, "failed git run leaves the package without versions");
        check(merged[5].Latest == "1.10.0" && merged[5].IsOutdated, "gitlab: numeric tag order");
        check(merged[1].Latest == null && merged[2].Latest == null && merged[4].Latest == null && merged[6].Latest == null, "path, commit, branch, unknown host get no versions");
        check(manager.ApplyOutdated(entries, new PackageToolOutput[0]).Count == entries.Count, "no outputs, unchanged");
        var mismatch = false;
        try { manager.ApplyOutdated(entries, outputs.Take(2).ToArray()); } catch (PackageManagerException) { mismatch = true; }
        check(mismatch, "output count that does not match is rejected");
    }

    private static void Edits(ZigPackageManager manager, string folder, string path, IReadOnlyList<PackageEntry> entries, Action<bool, string> check)
    {
        var add = manager.Add("C:\\zig\\zig.exe", path, new PackageRequest("mime", null, PackageScope.Runtime, "https://github.com/andrewrk/mime/archive/refs/tags/v3.1.0.tar.gz"));
        check(add.Edits.Count == 0 && add.Commands.Count == 1 && add.Commands[0].FileName == "C:\\zig\\zig.exe"
            && add.Commands[0].Arguments == "fetch --save=mime https://github.com/andrewrk/mime/archive/refs/tags/v3.1.0.tar.gz" && add.Commands[0].WorkingDirectory == folder, "add: " + add.Commands[0].Arguments);
        var rejected = 0;
        foreach (var call in new Func<PackageAction>[]
        {
            () => manager.Add("zig.exe", path, new PackageRequest("mime")),
            () => manager.Add("zig.exe", path, new PackageRequest("a/b", null, PackageScope.Runtime, "https://example.com/x.tar.gz")),
            () => manager.Update("zig.exe", path, entries[0], true),
            () => manager.Update("zig.exe", path, entries[1].WithVersions(null, "2", "2"), true),
            () => manager.Update("zig.exe", path, null, true),
            () => manager.Remove("zig.exe", path, new PackageEntry("nothere", "", PackageScope.Runtime, "", path, 1)),
        })
        {
            try { call(); } catch (PackageManagerException) { rejected++; }
        }
        check(rejected == 6, "PackageManagerException cases: " + rejected + " of 6");

        var removed = manager.Remove("zig.exe", path, entries[0]);
        var expected = string.Join("\n", Manifest.Split('\n').Where((l, i) => i < 5 || i > 8));
        check(removed.Commands.Count == 0 && removed.Edits.Count == 1 && removed.Edits[0].Text == expected, "remove a block with its trailing comment: " + removed.Edits[0].Text.Length + " vs " + expected.Length);
        var inline = manager.Remove("zig.exe", path, entries[1]).Edits[0].Text;
        check(inline == string.Join("\n", Manifest.Split('\n').Where((l, i) => i != 9)), "remove a one-line quoted dependency");
        var last = manager.Remove("zig.exe", path, entries[6]).Edits[0].Text;
        check(last == string.Join("\n", Manifest.Split('\n').Where((l, i) => i != 17)) && last.Contains(".paths = .{ \"build.zig\", \"src\" }"), "remove the last dependency");
        var crlf = WriteFile(folder, "crlf.zon", ".{\r\n    .dependencies = .{\r\n        .a = .{ .url = \"u\" },\r\n        .b = .{\r\n            .url = \"v\", // x\r\n        }\r\n    },\r\n}\r\n");
        var after = manager.Remove("zig.exe", crlf, manager.ReadManifest(crlf)[1]).Edits[0].Text;
        check(after == ".{\r\n    .dependencies = .{\r\n        .a = .{ .url = \"u\" },\r\n    },\r\n}\r\n", "remove without trailing comma keeps CRLF: " + after.Replace("\r", "\\r").Replace("\n", "\\n"));
        var tail = manager.Remove("zig.exe", crlf, manager.ReadManifest(crlf)[0]).Edits[0].Text;
        check(tail.Contains(".b = .{") && !tail.Contains(".a ="), "remove the first dependency");
        var nested = WriteFile(folder, "nested.zon", ".{ .dependencies = .{ .a = .{ .url = \"https://x/y,}.tar.gz\" }, .b = .{ .path = \"p\" } } }");
        check(manager.ReadManifest(nested).Select(e => e.Name).SequenceEqual(new[] { "a", "b" })
            && manager.Remove("zig.exe", nested, manager.ReadManifest(nested)[0]).Edits[0].Text == ".{ .dependencies = .{ .b = .{ .path = \"p\" } } }", "strings with braces and commas do not confuse the scanner");

        var current = entries[0].WithVersions("v3.0.0", "v3.1.0", "v3.1.0");
        var update = manager.Update("zig.exe", path, current, true);
        check(update.Commands.Count == 1 && update.Commands[0].Arguments == "fetch --save=mime https://github.com/andrewrk/mime/archive/refs/tags/v3.1.0.tar.gz", "update to latest replaces the tag: " + update.Commands[0].Arguments);
        var gitlab = manager.Update("zig.exe", path, entries[5].WithVersions("1.4.0", "1.10.0", "1.10.0"), true);
        check(gitlab.Commands[0].Arguments == "fetch --save=gitlab https://gitlab.com/g/s/r/-/archive/1.10.0/r-1.10.0.tar.gz", "gitlab tag appears twice: " + gitlab.Commands[0].Arguments);
        var viaGit = manager.Update("zig.exe", path, entries[3].WithVersions("v2.1.0", "v2.2.0", "v2.2.0"), true);
        check(viaGit.Commands[0].Arguments == "fetch --save-exact=viagit git+https://codeberg.org/o/lib.git#v2.2.0", "git+ keeps the tag: " + viaGit.Commands[0].Arguments);

        var pinned = manager.Update("zig.exe", path, entries[0], false);
        check(pinned.Commands.Count == 0 && pinned.Edits.Count == 0 && pinned.Description.Contains("pinned by hash"), "inside constraints: pinned by hash, nothing to run");
        var floating = manager.Update("zig.exe", path, entries[4], false);
        check(floating.Commands.Count == 1 && floating.Commands[0].Arguments == "fetch --save=branch https://github.com/o/br/archive/refs/heads/main.tar.gz", "branch archive can be fetched again");
        var all = manager.Update("zig.exe", path, null, false);
        check(all.Commands.Count == 1 && all.Commands[0].Arguments.Contains("--save=branch"), "update all: only the floating dependency");
        check(manager.Update("zig.exe", WriteFile(folder, "e.zon", ".{ .dependencies = .{ .a = .{ .path = \"p\" } } }"), null, false).Commands.Count == 0, "update all with nothing floating");
    }

    /// <summary>
    /// zig is not exercised (no zig fetch is ever run). When git is on PATH, the real <c>git ls-remote --tags --refs</c> that
    /// CreateOutdated builds is run against a public repository and its output is parsed. Returns whether zig itself exists.
    /// </summary>
    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check)
    {
        var git = ZigPackageManager.FindGit();
        if (git == null) Console.WriteLine("SKIP: git not found; live git checks skipped.");
        else
        {
            var folder = Path.Combine(root, "zig-live");
            Directory.CreateDirectory(folder);
            var path = WriteFile(folder, "build.zig.zon", ".{\n    .name = .app,\n    .dependencies = .{\n        .zig = .{ .url = \"https://github.com/ziglang/zig/archive/refs/tags/0.11.0.tar.gz\" },\n    },\n}\n");
            var manager = new ZigPackageManager();
            var declared = manager.ReadManifest(path);
            var commands = manager.CreateOutdated("zig.exe", path, declared);
            check(commands.Count == 1 && commands[0].Arguments.Contains("ls-remote --tags --refs https://github.com/ziglang/zig"), "live: one git command");
            if (commands.Count == 1)
            {
                var output = await RunAsync(commands[0]);
                check(output.ExitCode == 0, "live: git ls-remote succeeded: " + output.StandardError.Trim());
                var merged = manager.ApplyOutdated(declared, new[] { output });
                var latest = merged[0].Latest;
                check(latest != null && PackageVersion.Compare(latest, "0.11.0") > 0 && merged[0].Compatible == latest && merged[0].IsOutdated, "live: newest tag parsed: " + latest);
                Console.WriteLine("zig live (git part only, zig fetch not run): ziglang/zig newest tag " + latest);
            }
        }
        // Reaching zig itself needs zig.exe; the zig fetch commands are only built, never started.
        return Vistudio.Core.Zig.ZigToolchain.FindExecutable(null, Environment.GetEnvironmentVariable("PATH")) != null;
    }

    private static async Task<PackageToolOutput> RunAsync(ProcessStartInfo start)
    {
        using (var process = Process.Start(start)!)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(120000));
            if (!exited) { try { process.Kill(); } catch (InvalidOperationException) { } return new PackageToolOutput(-1, "", "timed out"); }
            return new PackageToolOutput(process.ExitCode, await output, await error);
        }
    }
}
