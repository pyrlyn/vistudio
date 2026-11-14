using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core;
using Vistudio.Core.Preview;

namespace Vistudio.Preview
{
    /// <summary>Outcome of one Gradle render of a file's previews.</summary>
    internal sealed class ComposePreviewRun
    {
        public ComposePreviewRun(string outputDirectory, IReadOnlyDictionary<string, ComposePreviewResult> results, string? error, bool canceled)
        { OutputDirectory = outputDirectory; Results = results; Error = error; Canceled = canceled; }
        public string OutputDirectory { get; }
        public IReadOnlyDictionary<string, ComposePreviewResult> Results { get; }
        /// <summary>A failure of the whole run (no JVM target, compile error, Gradle failure); per-preview errors are in Results.</summary>
        public string? Error { get; }
        public bool Canceled { get; }
    }

    /// <summary>
    /// Runs Build/ComposePreview.gradle for one Kotlin file. Gradle compiles the module's JVM target and the renderer runs in
    /// its own JVM; the IDE only scans the source, writes the request and reads PNG files and results.tsv.
    /// </summary>
    internal static class ComposePreviewRunner
    {
        public static async Task<ComposePreviewRun> RenderAsync(KotlinEnvironment environment, string sourceFile, ComposePreviewFile previews,
            CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var root = await environment.GetFolderAsync()
                ?? throw new InvalidOperationException("Open the Gradle project folder or its .kotlinproj to render Compose previews.");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var projectFile = environment.ProjectFileFor(root);
            var settings = await Task.Run(() => projectFile != null ? KotlinSettings.LoadForProjectFile(projectFile) : KotlinSettings.LoadForFolder(root), token);
            var gradleRoot = KotlinEnvironment.ResolveGradleRoot(root, settings.GradleRoot);
            var output = Path.Combine(gradleRoot, "build", "vistudio", "compose-preview", Key(sourceFile));
            var build = Path.Combine(Path.GetDirectoryName(typeof(ComposePreviewRunner).Assembly.Location)!, "Build");
            await Task.Run(() =>
            {
                // This directory belongs to the preview of this file only; results of the previous run are replaced.
                // Only the files are removed, so a folder kept open elsewhere (Explorer, a shell) does not block the render.
                Directory.CreateDirectory(output);
                foreach (var old in Directory.GetFiles(output, "p*.png").Concat(Directory.GetFiles(output, "*.tsv"))) File.Delete(old);
                File.WriteAllText(Path.Combine(output, "request.tsv"), previews.CreateRequest(), new UTF8Encoding(false));
            }, token);
            var start = GradleCommand.CreatePreview(gradleRoot, settings.GradleJavaHome, Path.Combine(build, "ComposePreview.gradle"),
                Path.Combine(build, "VistudioPreviewRenderer.java"), sourceFile, Path.Combine(output, "request.tsv"), output);
            environment.Log("Compose preview: rendering " + previews.Renderable.Count() + " preview(s) of " + sourceFile + " in " + gradleRoot);
            string? gradleError = null, compileError = null;
            int code;
            try
            {
                code = await OwnedProcess.RunAsync(start, line =>
                {
                    environment.Log(line);
                    gradleError ??= ComposePreviewResult.GradleError(line);
                    if (compileError == null && line.StartsWith("e: ", StringComparison.Ordinal)) compileError = line.Substring(3);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                environment.Log("Compose preview canceled.");
                return new ComposePreviewRun(output, new Dictionary<string, ComposePreviewResult>(), null, canceled: true);
            }
            var manifest = Path.Combine(output, "results.tsv");
            var results = code == 0 && File.Exists(manifest)
                ? ComposePreviewResult.ParseManifest(File.ReadAllText(manifest, Encoding.UTF8))
                : new Dictionary<string, ComposePreviewResult>();
            string? error = null;
            if (code != 0)
                error = gradleError ?? (compileError != null ? "Compilation failed: " + compileError
                    : "Gradle failed (exit " + code + "). See Output > Kotlin for details.");
            environment.Log(error == null ? "Compose preview: " + results.Values.Count(r => r.File != null) + " rendered, "
                + results.Values.Count(r => r.Error != null) + " failed." : "Compose preview failed: " + error);
            return new ComposePreviewRun(output, results, error, canceled: false);
        }

        private static string Key(string path)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
                return Path.GetFileNameWithoutExtension(path) + "-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
            }
        }
    }
}
