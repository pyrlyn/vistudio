using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vistudio;

internal static class DapSourceTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await DapSessionTests.RunAsync(check);
        var message = JObject.Parse(@"{""body"":{""stackFrames"":[{""source"":{""path"":""file:///c%3A/Code/Kotlin%20demo/Main.kt""},""line"":5}]},""message"":""café""}");
        var data = Encoding.UTF8.GetBytes(message.ToString());
        var input = new MemoryStream();
        var header = Encoding.ASCII.GetBytes("Content-Length: " + data.Length + "\r\n\r\n");
        for (var i = 0; i < 2; i++) { input.Write(header, 0, header.Length); input.Write(data, 0, data.Length); }
        input.Position = 0;
        var ended = 0;
        var stream = new KotlinDapSourceStream(input, () => ended++);
        var output = new MemoryStream();
        var buffer = new byte[3];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0) output.Write(buffer, 0, read);
        var bytes = output.ToArray();
        var offset = 0;
        for (var i = 0; i < 2; i++)
        {
            var text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var length = int.Parse(text.Substring(16, separator - 16));
            offset += separator + 4;
            var frame = JObject.Parse(Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
            check((string?)frame["body"]?["stackFrames"]?[0]?["source"]?["path"] == @"c:\Code\Kotlin demo\Main.kt", "DAP stack source URI becomes Windows path");
            check((int?)frame["body"]?["stackFrames"]?[0]?["line"] == 5 && (string?)frame["message"] == "café", "DAP positions and Unicode remain unchanged");
        }
        check(offset == bytes.Length && ended == 1, "DAP coalesced frames and EOF notification");
        var virtualSource = JObject.Parse(@"{""path"":""jrt:/java.base/Object.class""}");
        KotlinDapSourceStream.NormalizeSources(virtualSource);
        check((string?)virtualSource["path"] == "jrt:/java.base/Object.class", "Non-file DAP sources are preserved");
        foreach (var invalid in new[] { "Content-Length: 20000000\r\n\r\n", "Content-Length: 6\r\n\r\n{}", "Content-Length: 1\r\nContent-Length: 1\r\n\r\n{}" })
        {
            var closed = false;
            var broken = new KotlinDapSourceStream(new MemoryStream(Encoding.ASCII.GetBytes(invalid)), () => closed = true);
            try { await broken.ReadAsync(buffer, 0, 1); throw new Exception("Invalid DAP frame accepted"); }
            catch (Exception e) when (e is IOException || e is InvalidDataException) { check(closed, "Invalid DAP framing closes the session"); }
        }
    }
}
