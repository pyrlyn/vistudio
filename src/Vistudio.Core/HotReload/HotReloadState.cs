using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vistudio.Core.HotReload
{
    /// <summary>A class file whose bytes the running JVM may not have yet.</summary>
    public sealed class HotReloadCandidate
    {
        public HotReloadCandidate(HotReloadClassFile file, string hash) { File = file; Hash = hash; }
        public HotReloadClassFile File { get; }
        public string Hash { get; }
    }

    public sealed class HotReloadFailure
    {
        public HotReloadFailure(string className, string message) { ClassName = className; Message = message; }
        public string ClassName { get; }
        public string Message { get; }
    }

    /// <summary>What one reload did, or why it could not run.</summary>
    public sealed class HotReloadResult
    {
        public const string RestartAdvice = "The JVM swaps method bodies only. A change to a class's structure (an added or removed method, " +
            "field, lambda or supertype) needs a restart of the program.";

        private HotReloadResult(IReadOnlyList<string> reloaded, IReadOnlyList<string> notLoaded, IReadOnlyList<HotReloadFailure> failed, string? error)
        { Reloaded = reloaded; NotLoaded = notLoaded; Failed = failed; Error = error; }

        public IReadOnlyList<string> Reloaded { get; }
        /// <summary>Changed classes the program has not used yet; the JVM reads them from the class path when it first needs them.</summary>
        public IReadOnlyList<string> NotLoaded { get; }
        /// <summary>Classes the JVM refused; it keeps running their previous version.</summary>
        public IReadOnlyList<HotReloadFailure> Failed { get; }
        /// <summary>A failure of the whole reload: compilation, the missing JVM, the agent.</summary>
        public string? Error { get; }

        public static HotReloadResult NoChanges { get; } =
            new HotReloadResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HotReloadFailure>(), null);

        public static HotReloadResult Failure(string error) =>
            new HotReloadResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HotReloadFailure>(), error);

        /// <summary>The agent's result file. Without its final "done" line the agent did not finish, which is an error.</summary>
        public static HotReloadResult ParseAgent(string text)
        {
            var reloaded = new List<string>();
            var notLoaded = new List<string>();
            var failed = new List<HotReloadFailure>();
            string? error = null;
            var done = false;
            foreach (var raw in text.Split('\n'))
            {
                var fields = raw.TrimEnd('\r').Split('\t');
                if (fields[0] == "reloaded" && fields.Length == 2) reloaded.Add(fields[1]);
                else if (fields[0] == "notloaded" && fields.Length == 2) notLoaded.Add(fields[1]);
                else if (fields[0] == "failed" && fields.Length == 3) failed.Add(new HotReloadFailure(fields[1], Readable(fields[2])));
                else if (fields[0] == "error" && fields.Length == 2) error ??= Readable(fields[1]);
                else if (fields[0] == "done") done = true;
            }
            if (!done) error ??= "The reload agent did not finish in the program's JVM.";
            return new HotReloadResult(reloaded, notLoaded, failed, error);
        }

        // "UnsupportedOperationException: class redefinition failed: attempted to add a method" reads better without the type.
        private static string Readable(string message)
        {
            const string prefix = "UnsupportedOperationException: ";
            return message.StartsWith(prefix, StringComparison.Ordinal) ? message.Substring(prefix.Length) : message;
        }

        public string Summary
        {
            get
            {
                if (Error != null) return "Hot reload failed: " + Error;
                if (Reloaded.Count == 0 && NotLoaded.Count == 0 && Failed.Count == 0) return "Hot reload: no changed classes.";
                var parts = new List<string>();
                if (Reloaded.Count != 0) parts.Add(Count(Reloaded.Count) + " reloaded");
                if (NotLoaded.Count != 0) parts.Add(Count(NotLoaded.Count) + " not loaded yet (read from disk when first used)");
                if (Failed.Count != 0) parts.Add(Count(Failed.Count) + " could not be reloaded");
                return "Hot reload: " + string.Join(", ", parts) + ".";
            }
        }

        /// <summary>One line per refused class, then what to do about them.</summary>
        public IEnumerable<string> Details
        {
            get
            {
                foreach (var failure in Failed) yield return "  " + failure.ClassName + ": " + failure.Message;
                if (Failed.Count != 0) yield return "  " + RestartAdvice;
            }
        }

        private static string Count(int count) => count == 1 ? "1 class" : count + " classes";
    }

    /// <summary>
    /// What Vistudio knows about the class versions inside one running JVM. The JVM cannot be asked which bytes it holds,
    /// so the state is built from what was on disk and what each reload did.
    /// </summary>
    public sealed class HotReloadState
    {
        private sealed class Known
        {
            public long Length, Modified;
            /// <summary>Null for a file that was already there, unchanged, when it was first seen (never read).</summary>
            public string? Hash;
            /// <summary>The JVM refused these bytes and still runs an older version: offer the class again every time.</summary>
            public bool Refused;
        }

        private readonly Dictionary<string, Known> known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The classes to hand to the JVM after a compilation. A class seen before is a candidate when its bytes differ
        /// from what the JVM got. A class seen for the first time is one when this compilation changed its bytes (the
        /// first list of a session carries the hashes from before) or when it is newer than the JVM (compiled by another
        /// build while the program ran); otherwise the JVM started with it.
        /// </summary>
        /// <param name="jvmStart">The JVM's start in milliseconds since 1970-01-01 UTC.</param>
        public IReadOnlyList<HotReloadCandidate> Plan(HotReloadSnapshot before, HotReloadSnapshot after, long jvmStart, Func<string, string> hashOf)
        {
            var previous = new Dictionary<string, HotReloadClassFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in before.Classes) previous[file.Path] = file;
            var candidates = new List<HotReloadCandidate>();
            foreach (var file in after.Classes)
            {
                if (known.TryGetValue(file.Path, out var entry))
                {
                    if (!entry.Refused && entry.Length == file.Length && entry.Modified == file.Modified) continue;
                    if (!TryHash(hashOf, file.Path, out var hash)) continue;
                    // The compiler rewrote the file with the same bytes.
                    if (!entry.Refused && entry.Hash == hash) { entry.Length = file.Length; entry.Modified = file.Modified; continue; }
                    candidates.Add(new HotReloadCandidate(file, hash));
                    continue;
                }
                previous.TryGetValue(file.Path, out var old);
                var written = old == null || old.Length != file.Length || old.Modified != file.Modified;
                // What the JVM started with is the file as it was before this compilation, unless another build wrote it
                // while the program was already running.
                var startedWith = old != null && old.Modified <= jvmStart ? old : null;
                if (!written && startedWith != null)
                {
                    known[file.Path] = new Known { Length = file.Length, Modified = file.Modified, Hash = startedWith.Hash };
                    continue;
                }
                if (!TryHash(hashOf, file.Path, out var current)) continue;
                // Recompiled to the same bytes (a Kotlin file that only depends on the edited one).
                if (startedWith?.Hash != null && startedWith.Hash == current)
                {
                    known[file.Path] = new Known { Length = file.Length, Modified = file.Modified, Hash = current };
                    continue;
                }
                candidates.Add(new HotReloadCandidate(file, current));
            }
            return candidates;
        }

        private static bool TryHash(Func<string, string> hashOf, string path, out string hash)
        {
            // A class file deleted or rewritten between the listing and now is picked up by the next reload.
            try { hash = hashOf(path); return true; }
            catch (IOException) { hash = ""; return false; }
            catch (UnauthorizedAccessException) { hash = ""; return false; }
        }

        /// <summary>Records the outcome: reloaded and not-yet-loaded classes are current, refused ones stay pending.</summary>
        public void Apply(IReadOnlyList<HotReloadCandidate> candidates, HotReloadResult result)
        {
            // An agent that did not run changed nothing: the candidates stay unknown and are offered again.
            if (result.Error != null && result.Reloaded.Count == 0 && result.NotLoaded.Count == 0 && result.Failed.Count == 0)
            {
                foreach (var candidate in candidates) Record(candidate, refused: true);
                return;
            }
            var current = new HashSet<string>(result.Reloaded.Concat(result.NotLoaded), StringComparer.Ordinal);
            foreach (var candidate in candidates) Record(candidate, refused: !current.Contains(candidate.File.ClassName));
        }

        private void Record(HotReloadCandidate candidate, bool refused) =>
            known[candidate.File.Path] = new Known { Length = candidate.File.Length, Modified = candidate.File.Modified, Hash = candidate.Hash, Refused = refused };

        public int KnownCount => known.Count;
    }
}
