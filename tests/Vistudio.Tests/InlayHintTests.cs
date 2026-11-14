using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Newtonsoft.Json.Linq;
using Vistudio;
using Vistudio.Core;

internal static class InlayHintTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Settings(check);
        Normalize(check);
        ThemeColors(check);
        await WriterInjectsBetweenFramesAsync(check);
        await ReaderAnswersOwnedSectionsAsync(check);
    }

    private static void Settings(Action<bool, string> check)
    {
        var defaults = new KotlinSettings();
        var options = InlayHints.KotlinOptions(defaults);
        check(defaults.InlayHints == InlayHintsDisplay.Always, "Inlay hints are always shown by default");
        check(options.Count == 13 && options.All(o => o.Value), "Every Kotlin inlay hint kind is on by default");
        check(options.Select(o => o.Key).SequenceEqual(new[] { "hints.type.property", "hints.type.variable", "hints.type.function.return", "hints.type.function.parameter",
            "hints.lambda.return", "hints.lambda.receivers.parameters", "hints.value.ranges", "hints.value.kotlin.time", "hints.parameters", "hints.parameters.context",
            "hints.parameters.compiled", "hints.parameters.excluded", "hints.call.chains" }), "Kotlin option ids match Kotlin LSP 271");
        var section = InlayHintConfiguration.KotlinSection(new KotlinSettings { InlayHintsLocalVariableTypes = false, InlayHintsParameterNames = false });
        check((bool?)section["hints.type.variable"] == false && (bool?)section["hints.type.property"] == true, "Kotlin section is a flat object of option ids");
        check(new[] { "hints.parameters", "hints.parameters.context", "hints.parameters.compiled", "hints.parameters.excluded" }.All(id => (bool?)section[id] == false),
            "Turning off Kotlin parameter names turns off its sub-options");
        var java = InlayHintConfiguration.JavaInlayHints(new KotlinSettings { JavaInlayHintsParameterNames = JavaParameterNameHints.Literals, JavaInlayHintsFormatParameters = false });
        check((string?)java.SelectToken("parameterNames.enabled") == "literals" && (bool?)java.SelectToken("variableTypes.enabled") == true
            && (bool?)java.SelectToken("parameterTypes.enabled") == true && (bool?)java.SelectToken("formatParameters.enabled") == false, "Java java.inlayHints settings");
        check((string?)InlayHintConfiguration.JavaInlayHints(defaults).SelectToken("parameterNames.enabled") == "all", "Java parameter names default to all");
        check(InlayHints.EnableKind(InlayHintsDisplay.Always) == 0 && InlayHints.EnableKind(InlayHintsDisplay.Never) == 1 && InlayHints.EnableKind(InlayHintsDisplay.WhileHoldingAltF1) == 2,
            "Display modes map to Visual Studio InlayHintsEnableKind values");

        var serializer = new XmlSerializer(typeof(KotlinSettings));
        var writer = new StringWriter();
        serializer.Serialize(writer, new KotlinSettings { InlayHints = InlayHintsDisplay.Never, InlayHintsCallChains = false, JavaInlayHintsParameterNames = JavaParameterNameHints.None });
        var restored = (KotlinSettings)serializer.Deserialize(new StringReader(writer.ToString()));
        check(restored.InlayHints == InlayHintsDisplay.Never && !restored.InlayHintsCallChains && restored.InlayHintsRanges && restored.JavaInlayHintsParameterNames == JavaParameterNameHints.None,
            "Inlay hint settings survive the settings XML round trip");
        var legacy = (KotlinSettings)serializer.Deserialize(new StringReader("<KotlinSettings />"));
        check(legacy.InlayHints == InlayHintsDisplay.Always && InlayHints.KotlinOptions(legacy).All(o => o.Value), "Older settings files get every inlay hint kind");
    }

    private static void Normalize(Action<bool, string> check)
    {
        // Shapes returned by Kotlin LSP 271 (implicit it wrapped in whitespace hints, call chains without kind) and JDT LS.
        var position = new JObject { ["line"] = 27, ["character"] = 30 };
        var result = new JArray
        {
            new JObject { ["position"] = position.DeepClone(), ["label"] = new JArray(new JObject { ["value"] = " " }), ["kind"] = 1 },
            new JObject { ["position"] = position.DeepClone(), ["label"] = new JArray(new JObject { ["value"] = "it: " }, new JObject { ["value"] = "Int" }), ["kind"] = 1 },
            new JObject { ["position"] = position.DeepClone(), ["label"] = new JArray(new JObject { ["value"] = " " }), ["kind"] = 1 },
            new JObject { ["position"] = new JObject { ["line"] = 24, ["character"] = 29 }, ["label"] = new JArray(new JObject { ["value"] = "count =" }), ["kind"] = 2 },
            new JObject { ["position"] = new JObject { ["line"] = 21, ["character"] = 39 }, ["label"] = " List<Int>" },
            new JObject { ["position"] = new JObject { ["line"] = 33, ["character"] = 30 }, ["label"] = "a:", ["paddingRight"] = true },
            new JObject { ["position"] = new JObject { ["line"] = 34, ["character"] = 23 }, ["label"] = "Integer", ["kind"] = null },
        };
        var hints = (JArray)InlayHintConfiguration.Normalize(result)!;
        check(hints.Count == 5, "Whitespace-only hints are removed");
        check(hints[0]["label"]![0]!["value"]!.ToString() == "it: " && (bool?)hints[0]["paddingLeft"] == true && (bool?)hints[0]["paddingRight"] == true,
            "Whitespace-only hints become padding of the hint at the same position");
        check((int?)hints[1]["kind"] == 2 && hints[1]["paddingLeft"] == null, "Kotlin parameter hints keep their kind and get no padding");
        check((int?)hints[2]["kind"] == 1, "Hints without a kind become type hints (call chains)");
        check((int?)hints[3]["kind"] == 2 && (bool?)hints[3]["paddingRight"] == true, "Java name: hints become parameter hints");
        check((int?)hints[4]["kind"] == 1, "Java type hints with a null kind become type hints");
        check(InlayHintConfiguration.Normalize(null) == null && InlayHintConfiguration.Normalize(new JValue("x"))!.ToString() == "x", "Non-array results pass through");
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n").Concat(body).ToArray();
    }

    private static readonly byte[] Initialize = Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}");

    private static async Task WriterInjectsBetweenFramesAsync(Action<bool, string> check)
    {
        var output = new MemoryStream();
        using (var writer = new KotlinInitializeStream(output, p => p))
        {
            await writer.WriteAsync(Initialize, 0, Initialize.Length);
            var client = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"text\":\"abcdefghijklmnopqrstuvwxyz\"}}");
            var injected = Frame("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":[{}]}");
            var mark = output.Length;
            await writer.WriteAsync(client, 0, client.Length - 10); // header and part of the body
            await writer.InjectAsync(injected);
            check(output.Length - mark == client.Length - 10, "An injected frame waits while a client frame is incomplete");
            await writer.WriteAsync(client, client.Length - 10, 10);
            var written = output.ToArray().Skip((int)mark).ToArray();
            check(written.SequenceEqual(client.Concat(injected)), "The injected frame follows the completed client frame");

            mark = output.Length;
            await writer.InjectAsync(injected);
            check(output.ToArray().Skip((int)mark).SequenceEqual(injected), "At a frame boundary the injected frame is written at once");

            // Two client frames in one write, the second one split: the queued frame goes exactly between them.
            var second = Frame("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"textDocument/inlayHint\",\"params\":{}}");
            mark = output.Length;
            await writer.WriteAsync(client, 0, 5);
            await writer.InjectAsync(injected);
            var both = client.Skip(5).Concat(second).ToArray();
            await writer.WriteAsync(both, 0, both.Length);
            check(output.ToArray().Skip((int)mark).SequenceEqual(client.Concat(injected).Concat(second)), "Injected frames are placed between client frames of one write");
        }
    }

    private static async Task ReaderAnswersOwnedSectionsAsync(Action<bool, string> check)
    {
        var notification = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\",\"params\":{\"message\":\"workspace/configuration is mentioned\"}}");
        var owned = Frame("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"workspace/configuration\",\"params\":{\"items\":[{\"scopeUri\":\"file:///a.kt\",\"section\":\"jetbrains.kotlin\"}]}}");
        var foreign = Frame("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"workspace/configuration\",\"params\":{\"items\":[{\"section\":\"java\"}]}}");
        var last = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"$/progress\",\"params\":{}}");
        var server = new MemoryStream(notification.Concat(owned).Concat(foreign).Concat(last).ToArray());
        var toServer = new MemoryStream();
        var writer = new KotlinInitializeStream(toServer, p => p);
        await writer.WriteAsync(Initialize, 0, Initialize.Length);
        var mark = (int)toServer.Length;
        var reader = new LspConfigurationReader(server, writer, section => section == "jetbrains.kotlin" ? new JObject { ["hints.parameters"] = true } : null);
        var forwarded = new MemoryStream();
        var buffer = new byte[7]; // small reads cross frame borders
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0) forwarded.Write(buffer, 0, read);
        check(forwarded.ToArray().SequenceEqual(notification.Concat(foreign).Concat(last)), "Only requests for owned sections are kept from Visual Studio; all other frames pass unchanged");
        for (var i = 0; i < 100 && toServer.Length == mark; i++) await Task.Delay(20);
        var answer = Encoding.UTF8.GetString(toServer.ToArray(), mark, (int)toServer.Length - mark);
        var body = JObject.Parse(answer.Substring(answer.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
        check(answer.StartsWith("Content-Length: ") && (int?)body["id"] == 7 && (bool?)body.SelectToken("result[0]['hints.parameters']") == true,
            "The server gets the owned section answer for its request id");
    }

    // Registration/InlayHintColors.pkgdef: Dark-theme colors for Visual Studio's LSP inlay hint formats in the editor's
    // Fonts and Colors category (Visual Studio 18 files its own under Output Window, so editors get light boxes).
    private static void ThemeColors(Action<bool, string> check)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "Vistudio.Vsix", "Registration", "InlayHintColors.pkgdef");
        var lines = File.ReadAllLines(path);
        var themes = new System.Collections.Generic.Dictionary<string, byte[]>();
        string? key = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("[", StringComparison.Ordinal)) key = line;
            else if (key != null && line.StartsWith("\"Data\"=hex:", StringComparison.Ordinal))
                themes[key] = line.Substring(11).Split(',').Select(h => Convert.ToByte(h, 16)).ToArray();
        }
        check(themes.Count == 2, "Inlay hint colors cover Dark and Dark (Extra Contrast)");
        foreach (var (theme, background, foreground) in new[] { ("1ded0138-47ce-435e-84ef-9ec1f439b749", 0x3E, (byte)0xA9), ("c080e494-8a3f-417f-b846-46155da10c08", 0x33, (byte)0xC7) })
        {
            var entry = themes.FirstOrDefault(t => t.Key == @"[$RootKey$\Themes\{" + theme + @"}\Vistudio Text Editor MEF Items]").Value;
            check(entry != null, "Inlay hint colors for theme " + theme + " use their own key (built-in MEF item colors stay)");
            if (entry == null) continue;
            check(BitConverter.ToInt32(entry, 0) == entry.Length && BitConverter.ToInt32(entry, 4) == 11 && BitConverter.ToInt32(entry, 8) == 1,
                "Theme blob header: length, version 11, one category");
            check(new Guid(entry.Skip(12).Take(16).ToArray()) == new Guid("75a05685-00a8-4ded-bae5-e7a50bfa929a") && BitConverter.ToInt32(entry, 28) == 2,
                "Theme blob targets Text Editor MEF Items with two items");
            var p = 32;
            foreach (var name in new[] { "inlay hints type", "inlay hints parameter" })
            {
                var length = BitConverter.ToInt32(entry, p); p += 4;
                var actual = Encoding.UTF8.GetString(entry, p, length); p += length;
                var bg = entry.Skip(p).Take(5).ToArray(); p += 5;
                var fg = entry.Skip(p).Take(5).ToArray(); p += 5;
                check(actual == name && bg.SequenceEqual(new byte[] { 1, (byte)background, (byte)background, (byte)background, 0xFF }) && fg[0] == 1 && fg[1] == foreground && fg[4] == 0xFF,
                    "Theme " + theme + " gives '" + name + "' a dark background and light text");
            }
            check(p == entry.Length, "Theme blob has no trailing data");
        }
    }
}
