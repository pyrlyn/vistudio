using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio
{
    /// <summary>
    /// Readiness of the Kotlin language server for debugging: initialization and Gradle-import signals plus the reason of an
    /// unexpected process exit. A recorded exit fails pending and future waits at once, so F5 reports it instead of timing out.
    /// No Visual Studio dependencies; the client forwards process and protocol events here.
    /// </summary>
    internal sealed class KotlinServerLifecycle
    {
        private const int KeptErrorLines = 4;
        private readonly object gate = new object();
        private readonly Queue<string> recentErrors = new Queue<string>();
        private TaskCompletionSource<bool> ready = NewReady();
        private TaskCompletionSource<string?> imported = NewImported();
        private object? current, expectedExit;
        private string? failure;

        private static TaskCompletionSource<bool> NewReady() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource<string?> NewImported() => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Why the current server cannot serve requests, or null.</summary>
        public string? Failure { get { lock (gate) return failure; } }
        public bool IsReady { get { lock (gate) return ready.Task.Status == TaskStatus.RanToCompletion && imported.Task.Status == TaskStatus.RanToCompletion; } }

        /// <summary>A new server process starts: forget the previous failure and its output.</summary>
        public void Started(object process)
        {
            lock (gate)
            {
                current = process;
                expectedExit = null;
                failure = null;
                recentErrors.Clear();
                if (ready.Task.IsCompleted) ready = NewReady();
                if (imported.Task.IsCompleted) imported = NewImported();
            }
        }

        public void ErrorLine(object process, string line)
        {
            lock (gate)
            {
                if (!ReferenceEquals(process, current) || string.IsNullOrWhiteSpace(line)) return;
                recentErrors.Enqueue(line.Trim());
                while (recentErrors.Count > KeptErrorLines) recentErrors.Dequeue();
            }
        }

        /// <summary>The client is stopping this process on purpose (restart, workspace change, shutdown); its exit is not a failure.</summary>
        public void ExpectExit(object process)
        {
            lock (gate)
            {
                if (ReferenceEquals(process, current)) expectedExit = process;
            }
        }

        /// <summary>Clears signals after a deliberate stop; waiters see cancellation, as before.</summary>
        public void Reset()
        {
            lock (gate)
            {
                ready.TrySetCanceled();
                imported.TrySetCanceled();
                ready = NewReady();
                imported = NewImported();
            }
        }

        /// <summary>Records an exit; returns the failure message for an unexpected exit of the current process, otherwise null.</summary>
        public string? Exited(object process, int exitCode)
        {
            lock (gate)
            {
                if (!ReferenceEquals(process, current) || ReferenceEquals(process, expectedExit)) return null;
                var detail = recentErrors.Count == 0 ? "" : ": " + string.Join(" ", recentErrors);
                failure = "The Kotlin language server exited with code " + exitCode + detail
                    + " Fix the server in Tools > Options > Kotlin, then run Tools > Kotlin > Restart Language Server.";
                var error = new InvalidOperationException(failure);
                ready.TrySetException(error);
                imported.TrySetException(error);
                return failure;
            }
        }

        /// <summary>The current server cannot be used (for example LSP initialization failed); kept until the next start.</summary>
        public void Fail(string message)
        {
            lock (gate)
            {
                failure = message;
                var error = new InvalidOperationException(message);
                ready.TrySetException(error);
                imported.TrySetException(error);
            }
        }

        public void Initialized() { lock (gate) ready.TrySetResult(true); }

        public void ImportStarted() { lock (gate) if (imported.Task.IsCompleted && failure == null) imported = NewImported(); }

        public void ImportFinished(string? error) { lock (gate) imported.TrySetResult(error); }

        /// <summary>Waits for initialization and the server's Gradle import; throws at once when the server already exited.</summary>
        public async Task WaitForDebugAsync(CancellationToken token)
        {
            Task<bool> readyTask;
            Task<string?> importedTask;
            lock (gate)
            {
                if (failure != null) throw new InvalidOperationException(failure);
                readyTask = ready.Task;
                importedTask = imported.Task;
            }
            await WithCancellationAsync(readyTask, token).ConfigureAwait(false);
            var error = await WithCancellationAsync(importedTask, token).ConfigureAwait(false);
            if (error != null) throw new InvalidOperationException("Kotlin server Gradle import failed: " + error);
        }

#pragma warning disable VSTHRD003 // The signals are completed by protocol callbacks and process exits, never by a waiting UI thread.
        private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken token)
        {
            if (task.IsCompleted) return await task.ConfigureAwait(false);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task) throw new OperationCanceledException(token);
                return await task.ConfigureAwait(false);
            }
        }
#pragma warning restore VSTHRD003
    }
}
