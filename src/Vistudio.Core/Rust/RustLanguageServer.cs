using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Vistudio.Core.Languages;

namespace Vistudio.Core.Rust
{
    /// <summary>Discovery, launch and configuration of a user-installed rust-analyzer.</summary>
    public static class RustLanguageServer
    {
        public const string ExecutableName = "rust-analyzer";
        /// <summary>The <c>workspace/configuration</c> section rust-analyzer asks for; its value has the same shape as the initialization options.</summary>
        public const string ConfigurationSection = "rust-analyzer";

        public static string? Find(RustSettings settings) =>
            LanguageServerLocator.Find(settings.LanguageServerPath, ExecutableName, LanguageProjectDefaults.InstallFolders(LanguageCatalog.Rust));

        public static ProcessStartInfo Create(string executable, RustSettings settings, string workingDirectory) =>
            LanguageServerLocator.Create(executable, settings.LanguageServerArguments, workingDirectory, "rust-analyzer arguments");

        /// <summary>
        /// rust-analyzer reads its settings from <c>initializationOptions</c> as the contents of the <c>rust-analyzer</c>
        /// section (keys without the <c>rust-analyzer.</c> prefix). Only the inlay hint switches are set; everything else
        /// keeps rust-analyzer's defaults (cargo check on save included). Visual Studio's LSP client draws the hints itself.
        /// </summary>
        public static JObject Configuration(RustSettings settings)
        {
            JObject Enable() => new JObject { ["enable"] = settings.InlayHints };
            return new JObject
            {
                ["inlayHints"] = new JObject
                {
                    ["typeHints"] = Enable(),
                    ["parameterHints"] = Enable(),
                    ["chainingHints"] = Enable(),
                    ["closingBraceHints"] = Enable(),
                },
            };
        }
    }
}
