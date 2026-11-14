using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Languages;

namespace Vistudio.Core.Wasm
{
    /// <summary>Discovery, launch and configuration of a user-installed wat_server (wasm-language-tools).</summary>
    public static class WasmLanguageServer
    {
        public const string ExecutableName = "wat_server";
        /// <summary>
        /// The <c>workspace/configuration</c> section wat_server asks for per document when the client supports pulled
        /// configuration. It does not read <c>initializationOptions</c>, so the inlay hint switch reaches it only this way.
        /// </summary>
        public const string ConfigurationSection = "wasmLanguageTools";

        /// <summary>
        /// wat_server parses WebAssembly text modules only; WAST scripts (<c>.wast</c>) are colored by the grammar but not
        /// sent to it, and binary modules (<c>.wasm</c>) are not text.
        /// </summary>
        public static bool IsServerFile(string path) => string.Equals(Path.GetExtension(path ?? ""), ".wat", StringComparison.OrdinalIgnoreCase);

        public static string? Find(WasmSettings settings) =>
            LanguageServerLocator.Find(settings.LanguageServerPath, ExecutableName, LanguageProjectDefaults.InstallFolders(LanguageCatalog.Wasm));

        public static ProcessStartInfo Create(string executable, WasmSettings settings, string workingDirectory) =>
            LanguageServerLocator.Create(executable, settings.LanguageServerArguments, workingDirectory, "wat_server arguments");

        /// <summary>The section value: the three inlay hint kinds follow the one switch; omitted keys keep the server defaults.</summary>
        public static JObject Configuration(WasmSettings settings) => new JObject
        {
            ["inlayHint"] = new JObject
            {
                ["types"] = settings.InlayHints,
                ["ending"] = settings.InlayHints,
                ["index"] = settings.InlayHints,
            },
        };
    }
}
