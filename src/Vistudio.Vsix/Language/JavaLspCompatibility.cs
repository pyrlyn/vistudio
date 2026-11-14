using Newtonsoft.Json.Linq;

namespace Vistudio
{
    /// <summary>
    /// Eclipse JDT LS registers definition, completion and other text-document features through
    /// client/registerCapability whenever the client allows dynamic registration. Visual Studio accepts some of those
    /// registrations but never enables commands such as Go To Definition for them. Declaring dynamic registration
    /// unsupported makes JDT LS announce the features statically in its initialize result. Nothing else changes.
    /// </summary>
    internal static class JavaLspCompatibility
    {
        private static readonly string[] Features =
        {
            "completion", "hover", "signatureHelp", "declaration", "definition", "typeDefinition", "implementation",
            "references", "documentHighlight", "documentSymbol", "codeAction", "codeLens", "formatting",
            "rangeFormatting", "onTypeFormatting", "rename", "foldingRange", "selectionRange", "callHierarchy",
            "typeHierarchy", "semanticTokens", "inlayHint", "synchronization"
        };

        internal static JToken NormalizeInitialize(JToken parameters)
        {
            if (!(parameters is JObject root)) return parameters;
            var capabilities = root["capabilities"] as JObject ?? (JObject)(root["capabilities"] = new JObject());
            var textDocument = capabilities["textDocument"] as JObject ?? (JObject)(capabilities["textDocument"] = new JObject());
            foreach (var feature in Features)
            {
                var entry = textDocument[feature] as JObject ?? (JObject)(textDocument[feature] = new JObject());
                entry["dynamicRegistration"] = false;
            }
            return root;
        }
    }
}
