using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Vistudio.Core.Zig
{
    /// <summary>Process start information for every zig.exe invocation the extension makes. No shell is involved.</summary>
    public static class ZigCommandFactory
    {
        /// <summary>
        /// <c>zig env</c>. Zig prints its lib folder relative to the working directory when that is shorter (for example
        /// <c>lib\std</c> when run from its own folder), so callers resolve paths against <see cref="ProcessStartInfo.WorkingDirectory"/>.
        /// </summary>
        public static ProcessStartInfo Env(string zig) => Create(zig, new[] { "env" }, Path.GetTempPath());

        /// <summary><c>zig ast-check</c> reading the document text from stdin (so unsaved edits are checked too).</summary>
        public static ProcessStartInfo AstCheck(string zig, string file)
        {
            var arguments = new List<string> { "ast-check", "--color", "off" };
            if (IsZon(file)) arguments.Add("--zon");
            var directory = Path.GetDirectoryName(file);
            return Create(zig, arguments, string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? Path.GetDirectoryName(zig)! : directory!);
        }

        /// <summary><c>zig build-obj</c> for one file; the object goes to <c>zig-out/vistudio</c> under <paramref name="root"/>.</summary>
        public static ProcessStartInfo BuildObject(string zig, string file, string root, string? libDirectory, string flags, out string objectFile)
        {
            var output = Path.Combine(root, "zig-out", "vistudio");
            objectFile = Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".obj");
            var arguments = new List<string> { "build-obj", "--color", "off", file, "-femit-bin=" + objectFile };
            AddLibrary(arguments, libDirectory);
            arguments.AddRange(ZigArguments.Split(flags));
            return Create(zig, arguments, root);
        }

        /// <summary><c>zig run</c> for one file; program arguments follow <c>--</c>.</summary>
        public static ProcessStartInfo Run(string zig, string file, string root, string? libDirectory, string flags, string programArguments = "")
        {
            var arguments = new List<string> { "run", "--color", "off", file };
            AddLibrary(arguments, libDirectory);
            arguments.AddRange(ZigArguments.Split(flags));
            var program = ZigArguments.Split(programArguments);
            if (program.Count > 0) { arguments.Add("--"); arguments.AddRange(program); }
            return Create(zig, arguments, root);
        }

        /// <summary><c>zig build [step]</c> in the folder holding build.zig.</summary>
        public static ProcessStartInfo Build(string zig, string root, string? step, string? libDirectory, string flags)
        {
            var arguments = new List<string> { "build" };
            if (!string.IsNullOrWhiteSpace(step)) arguments.Add(ZigArguments.ValidateStep(step!.Trim()));
            arguments.Add("--color");
            arguments.Add("off");
            AddLibrary(arguments, libDirectory);
            arguments.AddRange(ZigArguments.Split(flags));
            return Create(zig, arguments, root);
        }

        /// <summary><c>zig build -l</c>: the build steps declared by build.zig.</summary>
        public static ProcessStartInfo ListSteps(string zig, string root, string? libDirectory)
        {
            var arguments = new List<string> { "build", "-l", "--color", "off" };
            AddLibrary(arguments, libDirectory);
            return Create(zig, arguments, root);
        }

        public static bool IsZigFile(string path) =>
            path.EndsWith(".zig", StringComparison.OrdinalIgnoreCase) || IsZon(path);

        public static bool IsZon(string path) => path.EndsWith(".zon", StringComparison.OrdinalIgnoreCase);

        /// <summary>The folder with build.zig at or above <paramref name="start"/>, stopping at <paramref name="limit"/> when given.</summary>
        public static string? FindBuildRoot(string start, string? limit = null)
        {
            var folder = File.Exists(start) ? Path.GetDirectoryName(start) : start;
            while (!string.IsNullOrEmpty(folder))
            {
                if (File.Exists(Path.Combine(folder, "build.zig"))) return folder;
                if (limit != null && string.Equals(folder!.TrimEnd('\\', '/'), limit.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
                folder = Path.GetDirectoryName(folder);
            }
            return null;
        }

        private static void AddLibrary(List<string> arguments, string? libDirectory)
        {
            if (string.IsNullOrEmpty(libDirectory)) return;
            arguments.Add("--zig-lib-dir");
            arguments.Add(libDirectory!);
        }

        private static ProcessStartInfo Create(string zig, IEnumerable<string> arguments, string directory)
        {
            var start = ProcessCommands.CreateRedirected(zig, ZigArguments.Join(arguments), directory);
            start.EnvironmentVariables["NO_COLOR"] = "1";
            start.EnvironmentVariables.Remove("CLICOLOR_FORCE");
            return start;
        }
    }
}
