using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Vistudio.Core
{
    /// <summary>
    /// Gives each Visual Studio instance its own Kotlin server system path. Kotlin LSP 271 lets only one server use a
    /// --system-path (it holds &lt;path&gt;\.app.lock), while the settings are shared by every instance. The configured path
    /// is slot 1; further instances use &lt;path&gt;-2, &lt;path&gt;-3, ... so a restarted instance reuses an existing cache.
    /// </summary>
    public static class KotlinSystemPath
    {
        public const string LockFileName = ".vistudio.lock";
        public const string ServerLockFileName = ".app.lock";
        public const int MaximumSlots = 64;
        private static readonly Regex Argument = new Regex(@"(?<![\w-])--system-path(?:=|\s+)(?:""(?<path>[^""]*)""|(?<path>[^\s""]+))", RegexOptions.CultureInvariant);

        /// <summary>The --system-path value in a server command line, or null.</summary>
        public static string? Find(string arguments)
        {
            var match = Argument.Match(arguments ?? "");
            return match.Success ? match.Groups["path"].Value : null;
        }

        /// <summary>The arguments with their --system-path replaced by (or extended with) the given path.</summary>
        public static string WithPath(string arguments, string path)
        {
            var value = "--system-path=\"" + path + "\"";
            var match = Argument.Match(arguments ?? "");
            if (match.Success) return arguments!.Substring(0, match.Index) + value + arguments.Substring(match.Index + match.Length);
            return string.IsNullOrWhiteSpace(arguments) ? value : arguments!.TrimEnd() + " " + value;
        }

        /// <summary>The official Kotlin LSP launchers, which accept --system-path.</summary>
        public static bool IsOfficialServer(string executable)
        {
            var name = Path.GetFileName(executable ?? "");
            return name.Equals("intellij-server.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("kotlin-lsp.cmd", StringComparison.OrdinalIgnoreCase)
                || name.Equals("kotlin-lsp.bat", StringComparison.OrdinalIgnoreCase);
        }

        public static string Slot(string basePath, int slot) =>
            slot <= 1 ? basePath : basePath.TrimEnd('\\', '/') + "-" + slot;

        /// <summary>
        /// Takes the first free slot and returns the server arguments for it. The lease keeps the slot until disposed
        /// (when the server stops; the operating system releases it if Visual Studio exits). Servers without a
        /// --system-path that are not the official launchers keep their arguments and get no slot.
        /// </summary>
        public static KotlinSystemPathLease Acquire(string executable, string arguments, string defaultBase)
        {
            var configured = Find(arguments);
            if (configured == null && !IsOfficialServer(executable)) return new KotlinSystemPathLease(arguments ?? "", null, 0, null);
            var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured ?? defaultBase));
            for (var slot = 1; slot <= MaximumSlots; slot++)
            {
                var path = Slot(basePath, slot);
                var lease = TryLock(path);
                if (lease == null) continue;
                if (ServerHolds(path)) { lease.Dispose(); continue; }
                return new KotlinSystemPathLease(WithPath(arguments ?? "", path), path, slot, lease);
            }
            throw new InvalidOperationException("All " + MaximumSlots + " Kotlin server system paths based on " + basePath + " are in use.");
        }

        private static FileStream? TryLock(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return new FileStream(Path.Combine(path, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // A server started without Vistudio's lock (another tool, an older Vistudio) holds its own lock file.
        private static bool ServerHolds(string path)
        {
            var file = Path.Combine(path, ServerLockFileName);
            if (!File.Exists(file)) return false;
            try { using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
    }

    /// <summary>Server arguments for one slot and the lock that reserves it.</summary>
    public sealed class KotlinSystemPathLease : IDisposable
    {
        private FileStream? lockFile;

        internal KotlinSystemPathLease(string arguments, string? systemPath, int slot, FileStream? lockFile)
        { Arguments = arguments; SystemPath = systemPath; Slot = slot; this.lockFile = lockFile; }

        public string Arguments { get; }
        /// <summary>The chosen system path, or null when the server takes no --system-path.</summary>
        public string? SystemPath { get; }
        public int Slot { get; }

        public void Dispose()
        {
            lockFile?.Dispose();
            lockFile = null;
        }
    }
}
