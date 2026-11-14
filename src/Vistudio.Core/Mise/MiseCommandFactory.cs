using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Mise
{
    public enum MiseAction
    {
        /// <summary><c>mise install</c>: installs the configured versions that are missing.</summary>
        Install,
        /// <summary><c>mise upgrade</c>: newest versions inside the requested ranges; the config is unchanged.</summary>
        Upgrade,
        /// <summary><c>mise upgrade --bump</c>: newest versions overall; rewrites the requested versions in the config.</summary>
        UpgradeToLatest,
        /// <summary><c>mise trust &lt;file&gt;</c>.</summary>
        Trust,
    }

    /// <summary>mise command lines (no prompts answered for the user) and their output capture.</summary>
    public static class MiseCommandFactory
    {
        // Tool names as mise accepts them: node, npm:@scope/pkg, aqua:BurntSushi/ripgrep, ubi:owner/repo[exe=x] is excluded on purpose.
        private static readonly Regex ToolName = new Regex(@"^[A-Za-z0-9@][A-Za-z0-9_.:@/+\-]*$", RegexOptions.CultureInvariant);

        public static string? FindExecutable(MiseSettings settings)
        {
            var configured = settings.MisePath.Trim().Trim('"');
            if (configured.Length > 0)
            {
                if (File.Exists(configured)) return Path.GetFullPath(configured);
                var inFolder = Path.Combine(configured, "mise.exe");
                return File.Exists(inFolder) ? Path.GetFullPath(inFolder) : null;
            }
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0)
                .Concat(new[]
                {
                    // Default install locations when Visual Studio started before PATH was updated.
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mise", "bin"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                });
            foreach (var directory in candidates)
            {
                try
                {
                    var path = Path.Combine(directory, "mise.exe");
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static ProcessStartInfo List(string mise, string directory) => Create(mise, directory, "ls", "--current", "--json");

        public static ProcessStartInfo Outdated(string mise, string directory, bool bump) =>
            bump ? Create(mise, directory, "outdated", "--bump", "--json") : Create(mise, directory, "outdated", "--json");

        /// <summary>The command for an action limited to <paramref name="tools"/> (the tools of the project or file it was started from).</summary>
        public static ProcessStartInfo Action(string mise, string directory, MiseAction action, IEnumerable<string> tools, string? file = null)
        {
            var names = tools.Distinct(StringComparer.Ordinal).Select(ValidateTool).ToArray();
            switch (action)
            {
                case MiseAction.Install: return Create(mise, directory, new[] { "install" }.Concat(names).ToArray());
                case MiseAction.Upgrade: return Create(mise, directory, new[] { "upgrade" }.Concat(names).ToArray());
                case MiseAction.UpgradeToLatest: return Create(mise, directory, new[] { "upgrade", "--bump" }.Concat(names).ToArray());
                case MiseAction.Trust:
                    if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("mise trust needs the config file.");
                    return Create(mise, directory, "trust", Path.GetFullPath(file));
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public static string ValidateTool(string name)
        {
            if (!ToolName.IsMatch(name)) throw new ArgumentException("Unsupported mise tool name: " + name);
            return name;
        }

        public static string Describe(MiseAction action) => action switch
        {
            MiseAction.Install => "Install missing",
            MiseAction.Upgrade => "Upgrade all",
            MiseAction.UpgradeToLatest => "Upgrade all to latest",
            _ => "Trust",
        };

        private static ProcessStartInfo Create(string mise, string directory, params string[] arguments)
        {
            var start = ProcessCommands.CreateRedirected(mise, string.Join(" ", arguments.Select(Quote)), directory);
            start.EnvironmentVariables["NO_COLOR"] = "1";
            start.EnvironmentVariables["MISE_COLOR"] = "0";
            // Never --yes / MISE_YES: mise would answer its own prompts, including "trust this config?", without the user.
            // Stdin is closed, so a prompt fails and its message reaches the Output pane instead.
            start.EnvironmentVariables.Remove("MISE_YES");
            return start;
        }

        /// <summary>Windows command-line quoting of one argument (CommandLineToArgvW rules).</summary>
        public static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                builder.Append(c);
            }
            builder.Append('\\', backslashes * 2).Append('"');
            return builder.ToString();
        }

        public readonly struct Captured
        {
            public Captured(int exitCode, string output, string error) { ExitCode = exitCode; Output = output; Error = error; }
            public int ExitCode { get; }
            public string Output { get; }
            public string Error { get; }
        }

        /// <summary>Runs a command to completion and returns its stdout and stderr; the process tree is killed on cancel or timeout.</summary>
        public static async Task<Captured> CaptureAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var owned = new OwnedProcess(start))
            {
                limit.CancelAfter(timeout);
                owned.Input.Close();
                var output = new StreamReader(owned.Output, Encoding.UTF8).ReadToEndAsync();
                var error = owned.Error.ReadToEndAsync();
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (limit.Token.Register(() => canceled.TrySetResult(true)))
                {
                    try
                    {
                        await Task.WhenAny(owned.Exit, canceled.Task).ConfigureAwait(false);
                        if (limit.IsCancellationRequested) throw new OperationCanceledException(limit.Token);
                        var code = await owned.Exit.ConfigureAwait(false);
                        await Task.WhenAll(output, error).ConfigureAwait(false);
                        return new Captured(code, output.Result, error.Result);
                    }
                    finally { await owned.StopAsync().ConfigureAwait(false); }
                }
            }
        }

        /// <summary>Collects the tools of <paramref name="directory"/>: what is configured and installed, then the in-range and latest updates.</summary>
        public static async Task<MiseReport> CollectAsync(string mise, string directory, CancellationToken token)
        {
            var timeout = TimeSpan.FromMinutes(2);
            var list = await CaptureAsync(List(mise, directory), timeout, token).ConfigureAwait(false);
            if (list.ExitCode != 0) return MiseReport.FromFailure(list.Error, list.ExitCode);
            var outdated = CaptureAsync(Outdated(mise, directory, bump: false), timeout, token);
            var bumped = CaptureAsync(Outdated(mise, directory, bump: true), timeout, token);
            string? updateError = null, inRange = null, latest = null;
            try
            {
                var results = await Task.WhenAll(outdated, bumped).ConfigureAwait(false);
                if (results[0].ExitCode == 0) inRange = results[0].Output; else updateError = MiseReport.FromFailure(results[0].Error, results[0].ExitCode).Error;
                if (results[1].ExitCode == 0) latest = results[1].Output; else updateError ??= MiseReport.FromFailure(results[1].Error, results[1].ExitCode).Error;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { updateError = "mise outdated timed out."; }
            try { return MiseReport.Parse(list.Output, inRange, latest, updateError); }
            catch (Newtonsoft.Json.JsonException e) { return MiseReport.Failed("Unexpected mise output: " + e.Message); }
        }
    }
}
