using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vistudio;

internal static class ServerLifecycleTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        async Task<Exception?> WaitError(KotlinServerLifecycle state, int milliseconds = 2000)
        {
            using (var timeout = new CancellationTokenSource(milliseconds))
            {
                try { await state.WaitForDebugAsync(timeout.Token); return null; }
                catch (Exception e) { return e; }
            }
        }

        // A pending debug wait fails as soon as the server exits unexpectedly, with its last stderr lines.
        var lifecycle = new KotlinServerLifecycle();
        var first = new object();
        lifecycle.Started(first);
        lifecycle.ErrorLine(first, "This build of kotlin-server has expired.");
        lifecycle.ErrorLine(first, "The IDE will now close.");
        var watch = Stopwatch.StartNew();
        var pending = WaitError(lifecycle, 60000);
        await Task.Delay(100);
        check(lifecycle.Exited(first, 7)?.Contains("code 7: This build of kotlin-server has expired. The IDE will now close.") == true, "An unexpected exit records the code and the server's last error lines");
        var error = await pending;
        check(error is InvalidOperationException && watch.Elapsed < TimeSpan.FromSeconds(5) && error.Message.Contains("Restart Language Server"),
            "A waiting debug launch fails immediately with the reason instead of timing out");
        watch.Restart();
        error = await WaitError(lifecycle, 60000);
        check(error is InvalidOperationException && watch.Elapsed < TimeSpan.FromSeconds(1), "A new debug launch after the exit fails at once");

        // A restart clears the failure; initialization and import then allow debugging.
        var second = new object();
        lifecycle.Started(second);
        check(lifecycle.Failure == null && !lifecycle.IsReady, "Starting a new server clears the previous failure");
        lifecycle.Initialized();
        lifecycle.ImportStarted();
        lifecycle.ImportFinished(null);
        check(lifecycle.IsReady && await WaitError(lifecycle) == null, "Initialized and imported servers accept debug launches");
        check(lifecycle.Exited(first, 1) == null && lifecycle.Failure == null, "The exit of a replaced process is ignored");

        // Deliberate stops are not failures; waiters see cancellation as before.
        lifecycle.ExpectExit(second);
        lifecycle.Reset();
        check(lifecycle.Exited(second, 0) == null && lifecycle.Failure == null, "Expected exits during restart or shutdown are not failures");
        error = await WaitError(lifecycle, 300);
        check(error is OperationCanceledException, "After a deliberate stop a launch waits for the next server instead of failing");

        // Initialization failure and Gradle import failure.
        var third = new object();
        lifecycle.Started(third);
        lifecycle.Fail("Kotlin LSP initialization failed.");
        check((await WaitError(lifecycle))?.Message == "Kotlin LSP initialization failed.", "Initialization failure is reported immediately");
        var fourth = new object();
        lifecycle.Started(fourth);
        lifecycle.Initialized();
        lifecycle.ImportFinished("Could not resolve plugin");
        check((await WaitError(lifecycle))?.Message == "Kotlin server Gradle import failed: Could not resolve plugin", "Server-side Gradle import failures keep their message");
        lifecycle.ImportStarted();
        check(!lifecycle.IsReady, "A new server import makes the server not ready again");
    }
}
