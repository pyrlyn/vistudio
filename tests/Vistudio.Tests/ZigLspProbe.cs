using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vistudio.Core;
using Vistudio.Core.Zig;

/// <summary>
/// Minimal stdio LSP client for the live ZLS check: the same launch as the extension (zls.json, --config-path) and the
/// requests the Visual Studio LSP client and the Zig CodeLens make. Server-to-client requests are answered with null.
/// </summary>
internal sealed class ZigLspProbe : IDisposable
{
    private readonly OwnedProcess process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken?>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JToken?>>();
    private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);
    private readonly Task reader;
    private int nextId;

    internal StringBuilder Log { get; } = new StringBuilder();

    internal ZigLspProbe(System.Diagnostics.ProcessStartInfo start)
    {
        // A console process with a UTF-8 code page (65001) gets Console.InputEncoding with a BOM, and .NET Framework
        // writes that preamble into every redirected stdin at Process.Start; ZLS then rejects the first frame
        // (MissingContentLength). Visual Studio has no console: its InputEncoding has no preamble (checked with a
        // console-less probe), so only this test host needs the fix.
        Console.InputEncoding = new UTF8Encoding(false);
        process = new OwnedProcess(start);
        _ = OwnedProcess.PumpAsync(process.Error, line => { lock (Log) Log.AppendLine("[stderr] " + line); });
        reader = Task.Run(ReadLoopAsync);
    }

    /// <param name="parameters">Null omits <c>params</c> (ZLS rejects <c>"params": {}</c> on <c>shutdown</c>).</param>
    internal async Task<JToken?> RequestAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) message["params"] = JToken.FromObject(parameters);
        await SendAsync(message);
        var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(60)));
        if (finished != completion.Task) throw new TimeoutException("ZLS did not answer " + method + ". Log:\n" + Log);
        return await completion.Task;
    }

    internal Task NotifyAsync(string method, object parameters) =>
        SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = JToken.FromObject(parameters) });

    private async Task SendAsync(JObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        await writeGate.WaitAsync();
        try
        {
            await process.Input.WriteAsync(header, 0, header.Length);
            await process.Input.WriteAsync(body, 0, body.Length);
            await process.Input.FlushAsync();
        }
        finally { writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var stream = process.Output;
        try
        {
            while (true)
            {
                var length = -1;
                string? line;
                while ((line = await ReadHeaderLineAsync(stream)) != null && line.Length > 0)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                if (line == null || length < 0) return;
                var buffer = new byte[length];
                for (var read = 0; read < length;)
                {
                    var n = await stream.ReadAsync(buffer, read, length - read);
                    if (n == 0) return;
                    read += n;
                }
                var message = JObject.Parse(Encoding.UTF8.GetString(buffer));
                var method = (string?)message["method"];
                if (method != null && message["id"] != null)
                {
                    // workspace/configuration, window/workDoneProgress/create, client/registerCapability, ...
                    await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"], ["result"] = JValue.CreateNull() });
                    lock (Log) Log.AppendLine("[server request] " + method);
                    continue;
                }
                if (method != null) { lock (Log) Log.AppendLine("[notification] " + method + " " + ((string?)message["params"]?["message"] ?? "")); continue; }
                if (message["id"] != null && pending.TryRemove((int)message["id"]!, out var completion))
                {
                    if (message["error"] != null) completion.TrySetException(new InvalidOperationException(message["error"]!.ToString(Formatting.None)));
                    else completion.TrySetResult(message["result"]);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, 0, 1) == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
    }

    public void Dispose()
    {
        try { process.StopAsync().Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        process.Dispose();
    }

    /// <summary>LSP position (0-based line, UTF-16 character) of the <paramref name="occurrence"/>-th match, plus <paramref name="offset"/>.</summary>
    internal static object Position(string text, string match, int offset = 0, int occurrence = 1)
    {
        var index = -1;
        for (var i = 0; i < occurrence; i++) index = text.IndexOf(match, index + 1, StringComparison.Ordinal);
        if (index < 0) throw new ArgumentException("Not found: " + match);
        index += offset;
        var line = text.Take(index).Count(c => c == '\n');
        var character = index - (text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1);
        return new { line, character };
    }
}
