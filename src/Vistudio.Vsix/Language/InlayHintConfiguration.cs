using System.Linq;
using Newtonsoft.Json.Linq;
using Vistudio.Core;

namespace Vistudio
{
    /// <summary>Inlay hint settings sent to the Kotlin and Java servers, and their results adapted for Visual Studio.</summary>
    internal static class InlayHintConfiguration
    {
        /// <summary>The Kotlin server's workspace/configuration answer for section jetbrains.kotlin: a flat object of option ids.</summary>
        public static JObject KotlinSection(KotlinSettings settings)
        {
            var section = new JObject();
            foreach (var option in InlayHints.KotlinOptions(settings)) section[option.Key] = option.Value;
            return section;
        }

        /// <summary>JDT LS java.inlayHints settings.</summary>
        public static JObject JavaInlayHints(KotlinSettings settings) => new JObject
        {
            ["parameterNames"] = new JObject { ["enabled"] = InlayHints.JavaParameterNames(settings) },
            ["variableTypes"] = new JObject { ["enabled"] = settings.JavaInlayHintsVariableTypes },
            ["parameterTypes"] = new JObject { ["enabled"] = settings.JavaInlayHintsParameterTypes },
            ["formatParameters"] = new JObject { ["enabled"] = settings.JavaInlayHintsFormatParameters }
        };

        /// <summary>
        /// Adapts a textDocument/inlayHint result for Visual Studio. Whitespace-only hints (the Kotlin server wraps
        /// implicit it/this hints in them) would render as empty boxes, so they become padding of the neighbouring hint
        /// at the same position. Hints without a kind (Kotlin call chains and ranges, every JDT LS hint) get one, so
        /// Visual Studio applies its parameter or type hint format: labels ending in ':' or '=' are parameter names.
        /// </summary>
        public static JToken? Normalize(JToken? result)
        {
            if (!(result is JArray hints)) return result;
            var normalized = new JArray();
            JObject? previous = null;
            JToken? padNext = null;
            foreach (var token in hints)
            {
                // A detached copy: JArray.Add would otherwise copy the hint and later padding would miss it.
                if (!(token.DeepClone() is JObject hint)) { normalized.Add(token.DeepClone()); continue; }
                var position = hint["position"];
                var text = LabelText(hint["label"]);
                if (text.Trim().Length == 0)
                {
                    if (previous != null && JToken.DeepEquals(previous["position"], position)) previous["paddingRight"] = true;
                    else padNext = position;
                    continue;
                }
                if (padNext != null && JToken.DeepEquals(padNext, position)) hint["paddingLeft"] = true;
                padNext = null;
                if (hint["kind"] == null || hint["kind"]!.Type == JTokenType.Null)
                {
                    var trimmed = text.TrimEnd();
                    hint["kind"] = trimmed.EndsWith(":") || trimmed.EndsWith("=") ? 2 : 1;
                }
                normalized.Add(hint);
                previous = hint;
            }
            return normalized;
        }

        private static string LabelText(JToken? label) => label switch
        {
            JValue value => (string?)value ?? "",
            JArray parts => string.Concat(parts.OfType<JObject>().Select(part => (string?)part["value"] ?? "")),
            _ => ""
        };
    }
}
