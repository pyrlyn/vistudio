using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vistudio.Core;
using Vistudio.Core.Packages;

internal static class PackagesGradleTests
{
    private const string KotlinBuild =
        "plugins { kotlin(\"jvm\") version \"2.4.21\" }\r\n" +
        "val ktorVersion = \"2.3.0\"\r\n" +
        "/* implementation(\"fake:block-comment:1.0\") */\r\n" +
        "dependencies {\r\n" +
        "    implementation(\"com.google.code.gson:gson:2.8.0\") // trailing comment\r\n" +
        "    api(\"org.slf4j:slf4j-api\")\r\n" +
        "    testImplementation(kotlin(\"test\"))\r\n" +
        "    implementation(kotlin(\"stdlib\"))\r\n" +
        "    implementation(\"io.ktor:ktor-client-core:$ktorVersion\")\r\n" +
        "    implementation(platform(\"org.jetbrains.kotlinx:kotlinx-coroutines-bom:1.7.0\"))\r\n" +
        "    implementation(libs.okhttp)\r\n" +
        "    implementation(libs.bundles.jackson)\r\n" +
        "    implementation(project(\":lib\"))\r\n" +
        "    implementation(files(\"libs/a.jar\"))\r\n" +
        "    kapt(\"com.google.dagger:dagger-compiler:2.40\")\r\n" +
        "    compileOnly(\"javax.annotation:jsr250-api:1.0\")\r\n" +
        "    implementation(\"junit:junit:4.12\") { exclude(group = \"org.hamcrest\") }\r\n" +
        "    // implementation(\"fake:line-comment:1.0\")\r\n" +
        "    val notADependency = \"fake:val:1.0\"\r\n" +
        "}\r\n" +
        "kotlin { sourceSets { val jvmTest by getting { dependencies { implementation(\"io.mockk:mockk:1.13.0\") } } } }\r\n" +
        "buildscript { dependencies { classpath(\"fake:classpath:1.0\") } }\r\n" +
        "val raw = \"\"\"implementation(\"fake:raw:1.0\")\"\"\"\r\n";

    private const string Catalog =
        "[versions]\n" +
        "okhttp = \"4.9.0\"   # pinned\n" +
        "jackson = { strictly = \"2.12.0\" }\n" +
        "\n" +
        "[libraries]\n" +
        "okhttp = { module = \"com.squareup.okhttp3:okhttp\", version.ref = \"okhttp\" }\n" +
        "jackson-core = { group = \"com.fasterxml.jackson.core\", name = \"jackson-core\", version.ref = \"jackson\" }\n" +
        "jackson-databind = \"com.fasterxml.jackson.core:jackson-databind:2.12.1\"\n" +
        "\n" +
        "[bundles]\n" +
        "jackson = [\"jackson-core\",\n" +
        "  \"jackson-databind\"]\n";

    private const string GroovyBuild =
        "plugins { id 'java-library' }\n" +
        "dependencies {\n" +
        "\timplementation 'com.google.guava:guava:30.0-jre'\n" +
        "\ttestImplementation \"junit:junit:4.13\"\n" +
        "\tapi group: 'org.apache.commons', name: 'commons-lang3', version: '3.11'\n" +
        "\timplementation \"org.foo:bar:${barVersion}\"\n" +
        "\timplementation platform('org.springframework.boot:spring-boot-dependencies:2.5.0')\n" +
        "\timplementation project(':app')\n" +
        "\t// implementation 'fake:comment:1'\n" +
        "\timplementation libs.okhttp\n" +
        "}\n";

    internal static void Run(string root, Action<bool, string> check)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(root, "gradle"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "settings.gradle.kts"), "include(\":app\", \":lib\", \":empty\")\n");
        var catalogPath = Path.Combine(root, "gradle", "libs.versions.toml");
        var kotlinPath = Path.Combine(root, "app", "build.gradle.kts");
        var groovyPath = Path.Combine(root, "lib", "build.gradle");
        var emptyPath = Path.Combine(root, "empty", "build.gradle.kts");
        File.WriteAllText(catalogPath, Catalog);
        File.WriteAllText(kotlinPath, KotlinBuild);
        File.WriteAllText(groovyPath, GroovyBuild);
        File.WriteAllText(emptyPath, "plugins { `java-library` }");
        var manager = new GradlePackageManager(Path.Combine(root, "Outdated.gradle"));

        check(manager.Ecosystem == PackageEcosystem.Gradle && manager.ManifestNames.SequenceEqual(new[] { "build.gradle.kts", "build.gradle" })
            && manager.ToolNames.SequenceEqual(new[] { "gradlew" }), "Gradle manager identity");
        check(string.Equals(GradlePackageManager.SettingsRoot(kotlinPath), root, StringComparison.OrdinalIgnoreCase), "Settings root is the folder with settings.gradle.kts");

        // ---- Kotlin DSL
        var entries = manager.ReadManifest(kotlinPath);
        PackageEntry? Find(IEnumerable<PackageEntry> list, string name) => list.FirstOrDefault(e => e.Name == name);
        check(!entries.Any(e => e.Name.StartsWith("fake:", StringComparison.Ordinal)), "Comments, strings, val and buildscript produce no entries: "
            + string.Join(", ", entries.Where(e => e.Name.StartsWith("fake:", StringComparison.Ordinal)).Select(e => e.Name)));
        var gson = Find(entries, "com.google.code.gson:gson");
        check(gson != null && gson.Requested == "2.8.0" && gson.Group == "implementation" && gson.Scope == PackageScope.Runtime && gson.Line == 5
            && gson.ManifestPath == kotlinPath && gson.Source == null, "Kotlin string coordinate with version and line");
        var slf4j = Find(entries, "org.slf4j:slf4j-api");
        check(slf4j != null && slf4j.Requested == "" && slf4j.Group == "api", "Coordinate without a version");
        var kotlinTest = Find(entries, "org.jetbrains.kotlin:kotlin-test");
        check(kotlinTest != null && kotlinTest.Source == "kotlin plugin" && kotlinTest.Scope == PackageScope.Development, "kotlin(\"test\") in testImplementation");
        check(Find(entries, "org.jetbrains.kotlin:kotlin-stdlib")?.Source == "kotlin plugin", "kotlin(\"stdlib\") shorthand");
        var ktor = Find(entries, "io.ktor:ktor-client-core");
        check(ktor != null && ktor.Source == "variable" && ktor.Requested == "$ktorVersion", "Template version is reported as a variable: " + ktor?.Requested);
        check(Find(entries, "org.jetbrains.kotlinx:kotlinx-coroutines-bom")?.Requested == "1.7.0", "platform(...) unwraps to its coordinate");
        var okhttp = Find(entries, "com.squareup.okhttp3:okhttp");
        check(okhttp != null && okhttp.Source == "version catalog" && okhttp.Requested == "4.9.0" && okhttp.ManifestPath == catalogPath && okhttp.Line == 2
            && okhttp.Group == "implementation", "Catalog alias points into the catalog's [versions] line");
        var jacksonCore = Find(entries, "com.fasterxml.jackson.core:jackson-core");
        var databind = Find(entries, "com.fasterxml.jackson.core:jackson-databind");
        check(jacksonCore != null && jacksonCore.Requested == "2.12.0" && jacksonCore.Line == 3 && databind != null && databind.Requested == "2.12.1" && databind.Line == 8,
            "Catalog bundle expands to its libraries (rich and string versions)");
        check(!entries.Any(e => e.Name.Contains("lib") && e.Name.StartsWith(":", StringComparison.Ordinal)) && entries.All(e => e.Name.Contains(":")), "project() and files() are skipped");
        check(Find(entries, "com.google.dagger:dagger-compiler")?.Scope == PackageScope.Build && Find(entries, "javax.annotation:jsr250-api")?.Scope == PackageScope.Build,
            "kapt and compileOnly are build scope");
        check(Find(entries, "junit:junit")?.Requested == "4.12", "Declaration with a configuration lambda");
        var mockk = Find(entries, "io.mockk:mockk");
        check(mockk != null && mockk.Scope == PackageScope.Development && mockk.Line == 21, "Nested kotlin { sourceSets { jvmTest { dependencies } } } is test scope");
        check(entries.Count == 13, "Kotlin entry count: " + entries.Count + " " + string.Join(", ", entries.Select(e => e.Name)));

        // ---- Groovy DSL
        var groovy = manager.ReadManifest(groovyPath);
        check(Find(groovy, "com.google.guava:guava")?.Requested == "30.0-jre" && Find(groovy, "com.google.guava:guava")?.Line == 3, "Groovy command-call coordinate");
        check(Find(groovy, "junit:junit")?.Scope == PackageScope.Development, "Groovy double-quoted coordinate in testImplementation");
        check(Find(groovy, "org.apache.commons:commons-lang3")?.Requested == "3.11", "Groovy map notation");
        check(Find(groovy, "org.foo:bar")?.Source == "variable" && Find(groovy, "org.foo:bar")?.Requested == "${barVersion}", "Groovy GString version is a variable");
        check(Find(groovy, "org.springframework.boot:spring-boot-dependencies")?.Requested == "2.5.0", "Groovy platform(...)");
        check(Find(groovy, "com.squareup.okhttp3:okhttp")?.Source == "version catalog", "Groovy catalog accessor");
        check(groovy.Count == 6, "Groovy entry count: " + groovy.Count);

        // ---- Update
        var tool = Path.Combine(root, "gradlew.bat");
        var update = manager.Update(tool, kotlinPath, gson!.WithVersions("2.8.0", null, "2.10.1"), true);
        check(update.Commands.Count == 0 && update.Edits.Count == 1 && update.Edits[0].Path == kotlinPath
            && update.Edits[0].Text == KotlinBuild.Replace("gson:2.8.0", "gson:2.10.1"), "Update rewrites only the version literal, CRLF preserved");
        update = manager.Update(tool, kotlinPath, okhttp!.WithVersions("4.9.0", null, "4.12.0"), true);
        check(update.Edits.Count == 1 && update.Edits[0].Path == catalogPath && update.Edits[0].Text == Catalog.Replace("okhttp = \"4.9.0\"", "okhttp = \"4.12.0\""),
            "Catalog-backed update rewrites the [versions] value, comment kept");
        update = manager.Update(tool, kotlinPath, jacksonCore!.WithVersions("2.12.0", null, "2.17.0"), true);
        check(update.Edits.Count == 1 && update.Edits[0].Text == Catalog.Replace("strictly = \"2.12.0\"", "strictly = \"2.17.0\""), "Rich version rewrites strictly");
        var groovyLang = Find(groovy, "org.apache.commons:commons-lang3")!;
        update = manager.Update(tool, groovyPath, groovyLang.WithVersions("3.11", null, "3.14.0"), true);
        check(update.Edits.Count == 1 && update.Edits[0].Text == GroovyBuild.Replace("'3.11'", "'3.14.0'"), "Groovy map-notation version rewritten");
        Throws(check, () => manager.Update(tool, kotlinPath, ktor!.WithVersions("2.3.0", null, "3.0.0"), true), "$ktorVersion", "Variable version cannot be updated and names the variable");
        Throws(check, () => manager.Update(tool, kotlinPath, Find(entries, "org.jetbrains.kotlin:kotlin-stdlib")!, true), "Kotlin Gradle plugin", "kotlin(...) cannot be updated");
        Throws(check, () => manager.Update(tool, kotlinPath, null, true), "Check for updates first", "Update all without versions asks for a check first");
        Throws(check, () => manager.Update(tool, kotlinPath, gson, true), "Check for updates first", "Update without a known latest asks for a check first");
        var stay = manager.Update(tool, kotlinPath, gson.WithVersions("2.8.0", null, "2.10.1"), false);
        check(stay.Edits.Count == 0 && stay.Commands.Count == 0 && stay.Description.Contains("exact"), "Update inside constraints changes nothing: " + stay.Description);
        var all = manager.UpdateAll(kotlinPath, new[]
        {
            gson.WithVersions("2.8.0", null, "2.10.1"), okhttp.WithVersions("4.9.0", null, "4.12.0"), databind!.WithVersions("2.12.1", null, "2.17.0"),
            ktor!.WithVersions("2.3.0", null, "3.0.0"), slf4j!.WithVersions("2.0.0", null, "2.0.0")
        });
        var catalogEdit = all.Edits.FirstOrDefault(e => e.Path == catalogPath);
        check(all.Edits.Count == 2 && all.Edits.Any(e => e.Path == kotlinPath && e.Text == KotlinBuild.Replace("gson:2.8.0", "gson:2.10.1"))
            && catalogEdit != null && catalogEdit.Text == Catalog.Replace("\"4.9.0\"", "\"4.12.0\"").Replace("databind:2.12.1", "databind:2.17.0"),
            "UpdateAll groups replacements into one edit per file");
        check(all.Description.Contains("not changed: io.ktor:ktor-client-core") && !all.Description.Contains("slf4j"), "UpdateAll names skipped entries: " + all.Description);

        // ---- Remove
        var remove = manager.Remove(tool, kotlinPath, gson);
        check(remove.Edits.Count == 1 && remove.Edits[0].Text == KotlinBuild.Replace("    implementation(\"com.google.code.gson:gson:2.8.0\") // trailing comment\r\n", ""),
            "Remove deletes the declaration line with its trailing comment");
        remove = manager.Remove(tool, kotlinPath, Find(entries, "junit:junit")!);
        check(remove.Edits[0].Text == KotlinBuild.Replace("    implementation(\"junit:junit:4.12\") { exclude(group = \"org.hamcrest\") }\r\n", ""), "Remove includes the configuration lambda");
        remove = manager.Remove(tool, kotlinPath, okhttp);
        check(remove.Edits.Count == 1 && remove.Edits[0].Path == kotlinPath && remove.Edits[0].Text == KotlinBuild.Replace("    implementation(libs.okhttp)\r\n", ""),
            "Catalog-backed remove edits the build file, not the catalog");
        remove = manager.Remove(tool, kotlinPath, mockk!);
        check(remove.Edits[0].Text == KotlinBuild.Replace("implementation(\"io.mockk:mockk:1.13.0\") ", ""), "Remove inside a one-line block removes only the statement");
        remove = manager.Remove(tool, groovyPath, Find(groovy, "com.google.guava:guava")!);
        check(remove.Edits[0].Text == GroovyBuild.Replace("\timplementation 'com.google.guava:guava:30.0-jre'\n", ""), "Groovy remove");
        Throws(check, () => manager.Remove(tool, kotlinPath, databind), "bundle", "Bundle member cannot be removed from the build file");
        Throws(check, () => manager.Remove(tool, kotlinPath, kotlinTest!), "kotlin(...)", "kotlin(...) cannot be removed");

        // ---- Add
        var add = manager.Add(tool, kotlinPath, new PackageRequest("com.example:new", "1.0"));
        var close = KotlinBuild.IndexOf("}\r\nkotlin {", StringComparison.Ordinal);
        check(add.Commands.Count == 0 && add.Edits.Count == 1 && add.Edits[0].Text == KotlinBuild.Insert(close, "    implementation(\"com.example:new:1.0\")\r\n"),
            "Kotlin add goes at the end of the top-level dependencies block with its indentation");
        add = manager.Add(tool, groovyPath, new PackageRequest("com.example:new", "1.0", PackageScope.Development));
        check(add.Edits[0].Text == GroovyBuild.Substring(0, GroovyBuild.Length - 2) + "\ttestImplementation 'com.example:new:1.0'\n}\n", "Groovy add uses tabs and testImplementation");
        add = manager.Add(tool, emptyPath, new PackageRequest("com.example:new", "1.0", PackageScope.Build));
        check(add.Edits[0].Text == "plugins { `java-library` }\n\ndependencies {\n    compileOnly(\"com.example:new:1.0\")\n}\n", "Add creates a dependencies block when missing");
        Throws(check, () => manager.Add(tool, kotlinPath, new PackageRequest("com.example:new")), "needs a version", "Add without a version is refused");
        Throws(check, () => manager.Add(tool, kotlinPath, new PackageRequest("com.google.code.gson:gson", "2.10.1")), "already declared", "Duplicate add is refused");
        Throws(check, () => manager.Add(tool, kotlinPath, new PackageRequest("gson", "2.10.1")), "group:artifact", "Name without a group is refused");
        try { manager.Add(tool, kotlinPath, new PackageRequest("com.example:x\"y", "1")); check(false, "Quote in a name accepted"); }
        catch (ArgumentException) { check(true, "Quote in a name is rejected before it reaches the build file"); }
        try { manager.Add(tool, kotlinPath, new PackageRequest("com.example:x", "$v")); check(false, "Template in a version accepted"); }
        catch (ArgumentException) { check(true, "Template in a version is rejected"); }

        // ---- Outdated
        File.WriteAllText(tool, "@echo off\r\n");
        File.WriteAllText(manager.OutdatedScript, "// placeholder\n");
        var commands = manager.CreateOutdated(tool, kotlinPath, entries);
        var command = commands.Single();
        check(command.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) && command.Arguments.StartsWith("/d /v:off /s /c \"\".\\gradlew.bat\"", StringComparison.Ordinal)
            && command.Arguments.Contains("--init-script \"" + manager.OutdatedScript + "\" " + GradlePackageManager.ReportTask)
            && command.Arguments.Contains("--no-configuration-cache") && command.Arguments.Contains("--no-daemon")
            && string.Equals(command.WorkingDirectory, root, StringComparison.OrdinalIgnoreCase),
            "Outdated command runs the wrapper with the init script: " + command.Arguments);
        check(command.EnvironmentVariables["VISTUDIO_OUTDATED_BUILD_FILE"] == kotlinPath && string.Equals(command.EnvironmentVariables["VISTUDIO_OUTDATED_ROOT"], root, StringComparison.OrdinalIgnoreCase)
            && command.EnvironmentVariables["VISTUDIO_OUTDATED_OUTPUT"] == GradlePackageManager.ResultFile(kotlinPath), "Outdated paths travel in environment variables");
        check(manager.CreateOutdated(tool, kotlinPath, Array.Empty<PackageEntry>()).Count == 0, "Nothing declared, nothing to run");
        Throws(check, () => manager.CreateOutdated("gradlew.bat", kotlinPath, entries), "absolute path", "Relative wrapper path is refused");
        var stdout = "> Task :app:vistudioOutdated6cc1f5c0\n" + GradlePackageManager.BeginMarker + "\nVISTUDIO-OUTDATED-GRADLE\t8.14.7\n"
            + "VISTUDIO-OUTDATED\t:app\timplementation\tcom.google.code.gson:gson\t2.8.0\t2.8.0\t2.13.1\n"
            + "VISTUDIO-OUTDATED\t:app\tapi\torg.slf4j:slf4j-api\t\t\t\n"
            + "VISTUDIO-OUTDATED-ERROR\t:app\torg.slf4j:slf4j-api: offline\n"
            + "VISTUDIO-OUTDATED\t:app\timplementation\tcom.squareup.okhttp3:okhttp\t4.9.0\t4.9.0\t5.0.0\n"
            + GradlePackageManager.EndMarker + "\n\nBUILD SUCCESSFUL in 3s\n";
        var applied = manager.ApplyOutdated(entries, new[] { new PackageToolOutput(0, stdout, "") });
        var appliedGson = Find(applied, "com.google.code.gson:gson")!;
        check(applied.Count == entries.Count && appliedGson.Current == "2.8.0" && appliedGson.Latest == "2.13.1" && appliedGson.IsOutdated
            && appliedGson.Caption == "com.google.code.gson:gson 2.8.0 \u2192 2.13.1", "Outdated report merged: " + appliedGson.Caption);
        check(Find(applied, "org.slf4j:slf4j-api")!.Latest == null && Find(applied, "org.slf4j:slf4j-api")!.Current == null, "Failed lookup leaves versions empty");
        check(Find(applied, "com.squareup.okhttp3:okhttp")!.Latest == "5.0.0", "Catalog entry matched by module name");
        var failure = "FAILURE: Build failed with an exception.\n\n* What went wrong:\nCould not resolve plugin org.jetbrains.kotlin.jvm.\n\n* Try:\n> Run with --info.\n";
        Throws(check, () => manager.ApplyOutdated(entries, new[] { new PackageToolOutput(1, "", failure) }), "Could not resolve plugin", "A failed run reports Gradle's message");
        check(ReferenceEquals(manager.ApplyOutdated(entries, Array.Empty<PackageToolOutput>()), entries), "No outputs leaves entries unchanged");

        // ---- Catalog errors are reported, not swallowed
        File.WriteAllText(catalogPath, "[libraries]\nbad = { module = \"a:b\", version.ref = \"missing\" }\n");
        Throws(check, () => manager.ReadManifest(kotlinPath), "missing", "Unknown version.ref is a manifest error");
        File.WriteAllText(catalogPath, Catalog);
    }

    private static void Throws(Action<bool, string> check, Action action, string fragment, string message)
    {
        try { action(); check(false, message + " (no exception)"); }
        catch (PackageManagerException e) { check(e.Message.IndexOf(fragment, StringComparison.Ordinal) >= 0, message + ": " + e.Message); }
    }

    internal static async Task<bool> RunLiveAsync(string root, Action<bool, string> check, string repositoryRoot)
    {
        var jdk = new[] { Environment.GetEnvironmentVariable("VISTUDIO_TEST_JDK"), @"C:\Program Files\Eclipse Adoptium\jdk-21.0.12.101-hotspot", Environment.GetEnvironmentVariable("JAVA_HOME") }
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "bin", "java.exe")));
        var sample = Path.Combine(repositoryRoot, "samples", "kotlin-jvm");
        var script = Path.Combine(repositoryRoot, "src", "Vistudio.Vsix", "Build", "OutdatedDependencies.gradle");
        if (jdk == null || !File.Exists(Path.Combine(sample, "gradlew.bat")) || !File.Exists(Path.Combine(sample, "gradle", "wrapper", "gradle-wrapper.jar")) || !File.Exists(script))
            return false;
        // Gradle never runs inside the repository share: the sample's build files and wrapper are copied to a local folder.
        var folder = Path.Combine(Path.GetFullPath(root), "live");
        Directory.CreateDirectory(Path.Combine(folder, "gradle", "wrapper"));
        foreach (var name in new[] { "build.gradle.kts", "settings.gradle.kts", "gradlew.bat" }) File.Copy(Path.Combine(sample, name), Path.Combine(folder, name), true);
        foreach (var file in Directory.GetFiles(Path.Combine(sample, "gradle", "wrapper"))) File.Copy(file, Path.Combine(folder, "gradle", "wrapper", Path.GetFileName(file)), true);
        var manifest = Path.Combine(folder, "build.gradle.kts");
        var tool = Path.Combine(folder, "gradlew.bat");
        var manager = new GradlePackageManager(script, jdk);

        Apply(manager.Add(tool, manifest, new PackageRequest("com.google.code.gson:gson", "2.8.0")));
        var declared = manager.ReadManifest(manifest);
        check(declared.Count == 1 && declared[0].Name == "com.google.code.gson:gson" && declared[0].Requested == "2.8.0", "Live: old gson added to the sample copy");

        var start = manager.CreateOutdated(tool, manifest, declared).Single();
        var clock = Stopwatch.StartNew();
        var output = await RunAsync(start, TimeSpan.FromMinutes(10));
        clock.Stop();
        Console.WriteLine("Live Gradle outdated run: exit " + output.ExitCode + " in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s");
        var block = output.StandardOutput.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l != GradlePackageManager.BeginMarker).TakeWhile(l => l != GradlePackageManager.EndMarker).ToList();
        foreach (var line in block) Console.WriteLine("  " + line.Replace('\t', ' '));
        if (block.Count == 0) Console.WriteLine(output.StandardOutput + Environment.NewLine + output.StandardError);
        check(output.ExitCode == 0, "Live: the outdated run succeeds");
        var preReleases = block.Where(l => l.StartsWith("VISTUDIO-OUTDATED\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t')).Where(f => f.Length >= 7 && System.Text.RegularExpressions.Regex.IsMatch(f[6], @"(?i)[.-](rc|beta|alpha|m)\d*$")).ToList();
        check(preReleases.Count == 0, "Live: stable declarations are never offered a pre-release: " + string.Join(", ", preReleases.Select(f => f[3] + " " + f[6])));
        var checkedEntries = manager.ApplyOutdated(declared, new[] { output });
        var gson = checkedEntries.Single();
        Console.WriteLine("Live gson: requested " + gson.Requested + ", current " + gson.Current + ", latest " + gson.Latest);
        check(gson.Current == "2.8.0" && gson.Latest != null && PackageVersion.Compare(gson.Latest, "2.8.0") > 0 && !PackageVersion.IsPreRelease(gson.Latest) && gson.IsOutdated,
            "Live: Gradle reports a newer gson release than 2.8.0");
        var resultFile = GradlePackageManager.ResultFile(manifest);
        check(File.Exists(resultFile) && File.ReadAllText(resultFile).Contains("com.google.code.gson:gson\t2.8.0"), "Live: the result file is written too");

        var original = File.ReadAllText(manifest);
        Apply(manager.Update(tool, manifest, gson, true));
        var reread = manager.ReadManifest(manifest).Single();
        check(reread.Requested == gson.Latest && File.ReadAllText(manifest) == original.Replace("gson:2.8.0", "gson:" + gson.Latest), "Live: update rewrote the version to " + reread.Requested);
        return true;
    }

    private static void Apply(PackageAction action)
    {
        foreach (var edit in action.Edits) File.WriteAllText(edit.Path, edit.Text);
    }

    private static async Task<PackageToolOutput> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using (var process = new OwnedProcess(start))
        {
            process.Input.Close();
            var stdout = new StreamReader(process.Output, Encoding.UTF8).ReadToEndAsync();
            var stderr = process.Error.ReadToEndAsync();
            if (await Task.WhenAny(process.Exit, Task.Delay(timeout)) != process.Exit)
            {
                await process.StopAsync();
                throw new TimeoutException("Gradle did not finish within " + timeout);
            }
            var code = await process.Exit;
            return new PackageToolOutput(code, await stdout, await stderr);
        }
    }
}
