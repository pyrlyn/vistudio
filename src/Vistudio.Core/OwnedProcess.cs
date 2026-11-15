using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core
{
    // Owns one process tree, never a machine-wide Java or Gradle process name.
    public sealed class OwnedProcess : IDisposable
    {
        private readonly Process process;
        private readonly ProcessJob job = new ProcessJob();
        private readonly TaskCompletionSource<int> exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool disposed;
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public StreamReader Error => process.StandardError;
        public Task<int> Exit => exited.Task;
        public int Id => process.Id;

        public OwnedProcess(ProcessStartInfo start)
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, __) => exited.TrySetResult(process.ExitCode);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Could not start " + start.FileName);
                if (!process.HasExited) job.Assign(process.Handle);
                if (process.HasExited) exited.TrySetResult(process.ExitCode);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                job.Dispose(); process.Dispose(); throw;
            }
        }

        public async Task StopAsync()
        {
            if (disposed) return;
            job.Terminate();
            if (process.HasExited) return;
            // --no-daemon keeps each Gradle invocation's worker in its own tree.
            using (var killer = new Process
            {
                StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    "/PID " + process.Id + " /T /F")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
            })
            {
                killer.Start();
                var output = killer.StandardOutput.ReadToEndAsync();
                var error = killer.StandardError.ReadToEndAsync();
                await Task.WhenAll(output, error).ConfigureAwait(false);
                await Task.Run(() => killer.WaitForExit()).ConfigureAwait(false);
            }
            if (await Task.WhenAny(Exit, Task.Delay(5000)).ConfigureAwait(false) != Exit)
                throw new IOException("The owned process could not be stopped. PID: " + process.Id);
        }

        public static Task<int> RunAsync(ProcessStartInfo start, Action<string> onLine, CancellationToken token) =>
            RunAsync(start, onLine, onLine, token);

        /// <summary>Runs to completion with standard output and error kept apart (machine-readable output on one, progress on the other).</summary>
        /// <param name="started">Gets the process id once it runs (diagnostics watch the program a Run command starts).</param>
        public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> onOutput, Action<string> onError, CancellationToken token, Action<int>? started = null)
        {
            token.ThrowIfCancellationRequested();
            using (var owned = new OwnedProcess(start))
            {
                owned.process.StandardInput.Close();
                started?.Invoke(owned.process.Id);
                var stdout = PumpAsync(owned.process.StandardOutput, onOutput);
                var stderr = PumpAsync(owned.process.StandardError, onError);
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    try
                    {
                        await Task.WhenAny(owned.Exit, canceled.Task).ConfigureAwait(false);
                        if (token.IsCancellationRequested) await owned.StopAsync().ConfigureAwait(false);
                        var code = await owned.Exit.ConfigureAwait(false);
                        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        return code;
                    }
                    finally { await owned.StopAsync().ConfigureAwait(false); }
                }
            }
        }

        public static async Task PumpAsync(StreamReader reader, Action<string> onLine)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) onLine(line);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            job.Dispose();
            process.Dispose();
        }
    }
}
