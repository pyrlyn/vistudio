using System.Linq;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    internal static class KotlinLspCompatibility
    {
        public static JToken NormalizeInitialize(JToken parameters)
        {
            var result = parameters.DeepClone();
            // VS includes kind 0 and private enum values. Kotlin's strict enum decoder rejects them.
            FilterEnum(result.SelectToken("capabilities.textDocument.completion.completionItemKind.valueSet"), 25);
            FilterEnum(result.SelectToken("capabilities.textDocument.publishDiagnostics.tagSupport.valueSet"), 2);
            return result;
        }

        private static void FilterEnum(JToken? token, int maximum)
        {
            if (!(token is JArray values)) return;
            foreach (var value in values.ToArray())
                if (value.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out var number) || number < 1 || number > maximum)
                    value.Remove();
        }
    }
}
