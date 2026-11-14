using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Vistudio.Core.Packages;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Languages
{
    /// <summary>
    /// Finds and launches user-installed language servers that need no Vistudio-specific setup (rust-analyzer,
    /// wat_server): the Tools &gt; Options path first, then PATH, then the language's installer folders (for a Visual
    /// Studio started before PATH was updated).
    /// </summary>
    public static class LanguageServerLocator
    {
        /// <param name="configured">The configured executable or its folder; empty searches PATH and <paramref name="installFolders"/>.</param>
        /// <returns>The full path, or null when nothing was found (a configured path that does not exist is not replaced by PATH).</returns>
        public static string? Find(string? configured, string executableName, IEnumerable<string> installFolders)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    var value = Environment.ExpandEnvironmentVariables(configured!.Trim().Trim('"'));
                    if (Directory.Exists(value)) value = Path.Combine(value, executableName + ".exe");
                    return Path.IsPathRooted(value) && File.Exists(value) ? Path.GetFullPath(value) : null;
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
            }
            // ToolLocator searches its extra folders before PATH; PATH wins here, as it does in a terminal.
            return ToolLocator.Find(new[] { executableName }) ?? ToolLocator.Find(new[] { executableName }, installFolders);
        }

        /// <summary>Start information for a stdio server; <paramref name="arguments"/> are re-quoted after validation.</summary>
        public static ProcessStartInfo Create(string executable, string arguments, string workingDirectory, string argumentsSubject) =>
            ProcessCommands.LanguageServer(executable, ZigArguments.Join(ZigArguments.Split(arguments, argumentsSubject)), workingDirectory);
    }
}
