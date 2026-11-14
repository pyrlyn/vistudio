using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Projects
{
    public static class GradleProjectImporter
    {
        public static async Task<GradleProjectModel> ResolveAsync(KotlinProjectFile project, KotlinSettings defaults,
            string script, Action<string> log, CancellationToken token)
        {
            project.ApplyTo(defaults);
            var output = Path.Combine(Path.GetTempPath(), "vistudio-model-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var start = GradleCommand.CreateModelImport(project.GradleRoot, defaults.GradleJavaHome, script, output);
                start.EnvironmentVariables["VISTUDIO_MODEL_CONFIGURATIONS"] = project.Get("ImportConfigurations");
                var exit = await OwnedProcess.RunAsync(start, log, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (exit != 0) throw new InvalidOperationException("Gradle model import failed (exit " + exit + "). Previous metadata is unchanged. See Kotlin Output.");
                return await Task.Run(() => GradleProjectModel.Load(output, project.GradleRoot), token).ConfigureAwait(false);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
