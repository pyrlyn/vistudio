using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    // Incomplete scratch expressions must not publish errors into the user's Error List.
    // Inlay hint results are adapted for Visual Studio (see InlayHintConfiguration.Normalize).
    internal sealed class KotlinDebugDocumentFilter : ILanguageClientMiddleLayer2<JToken>
    {
        public bool CanHandle(string methodName) => methodName == "textDocument/publishDiagnostics" || methodName == InlayHintMiddleLayer.Method;
        public Task HandleNotificationAsync(string methodName, JToken parameters, Func<JToken, Task> sendNotification) =>
            ((string?)parameters["uri"] ?? "").IndexOf("/" + KotlinSemanticCompletion.FilePrefix, StringComparison.Ordinal) >= 0
                ? Task.CompletedTask : sendNotification(parameters);
        public Task<JToken?> HandleRequestAsync(string methodName, JToken parameters, Func<JToken, Task<JToken?>> sendRequest) =>
            methodName == InlayHintMiddleLayer.Method ? InlayHintMiddleLayer.NormalizeAsync(parameters, sendRequest) : sendRequest(parameters);
    }

    /// <summary>Adapts textDocument/inlayHint results for Visual Studio (Java client; the Kotlin filter reuses it).</summary>
    internal sealed class InlayHintMiddleLayer : ILanguageClientMiddleLayer2<JToken>
    {
        internal const string Method = "textDocument/inlayHint";
        public bool CanHandle(string methodName) => methodName == Method;
        public Task HandleNotificationAsync(string methodName, JToken parameters, Func<JToken, Task> sendNotification) => sendNotification(parameters);
        public Task<JToken?> HandleRequestAsync(string methodName, JToken parameters, Func<JToken, Task<JToken?>> sendRequest) =>
            methodName == Method ? NormalizeAsync(parameters, sendRequest) : sendRequest(parameters);
        internal static async Task<JToken?> NormalizeAsync(JToken parameters, Func<JToken, Task<JToken?>> sendRequest) =>
            InlayHintConfiguration.Normalize(await sendRequest(parameters).ConfigureAwait(false));
    }
}
