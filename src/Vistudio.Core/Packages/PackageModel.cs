using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Vistudio.Core.Packages
{
    public enum PackageEcosystem { Pub, Cargo, Gradle, Conan, Zig, Wkg }

    public enum PackageScope { Runtime, Development, Build }

    /// <summary>
    /// One package a manifest declares, with the versions its manager reported. <see cref="Current"/>,
    /// <see cref="Compatible"/> and <see cref="Latest"/> stay null until an outdated report supplied them:
    /// a declared constraint is never presented as a resolved version.
    /// </summary>
    public sealed class PackageEntry
    {
        public PackageEntry(string name, string requested, PackageScope scope, string group, string manifestPath, int line,
            string? source = null, string? current = null, string? compatible = null, string? latest = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Requested = requested ?? "";
            Scope = scope;
            Group = group ?? "";
            ManifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
            Line = line;
            Source = source;
            Current = Empty(current);
            Compatible = Empty(compatible);
            Latest = Empty(latest);
        }

        public string Name { get; }

        /// <summary>The version or constraint exactly as the manifest writes it; empty when it gives none.</summary>
        public string Requested { get; }

        public PackageScope Scope { get; }

        /// <summary>The manifest section the entry sits in, in the manager's own words (<c>dev_dependencies</c>, <c>implementation</c>, <c>[requires]</c>).</summary>
        public string Group { get; }

        public string ManifestPath { get; }

        /// <summary>1-based line of the declaration in <see cref="ManifestPath"/>; 0 when unknown.</summary>
        public int Line { get; }

        /// <summary>Where the package comes from when that is not the default registry: a URL, a path, <c>git</c>, <c>sdk</c>.</summary>
        public string? Source { get; }

        /// <summary>The version in use now (lock file or resolution).</summary>
        public string? Current { get; }

        /// <summary>The newest version <see cref="Requested"/> still allows.</summary>
        public string? Compatible { get; }

        /// <summary>The newest version the registry has.</summary>
        public string? Latest { get; }

        /// <summary>A newer version exists, inside or outside the declared constraint.</summary>
        public bool IsOutdated => Newer(Latest) || Newer(Compatible);

        /// <summary>A newer version exists that needs no manifest change.</summary>
        public bool HasCompatibleUpdate => Newer(Compatible);

        public PackageEntry WithVersions(string? current, string? compatible, string? latest) =>
            new PackageEntry(Name, Requested, Scope, Group, ManifestPath, Line, Source, current, compatible, latest);

        /// <summary>Solution Explorer text: <c>name 1.2.0</c>, or <c>name 1.2.0 → 1.4.1</c> when outdated.</summary>
        public string Caption
        {
            get
            {
                var shown = Current ?? Requested;
                var text = shown.Length == 0 ? Name : Name + " " + shown;
                var target = Newer(Latest) ? Latest : Newer(Compatible) ? Compatible : null;
                return target == null ? text : text + " → " + target;
            }
        }

        private bool Newer(string? candidate) =>
            candidate != null && Current != null && PackageVersion.Compare(candidate, Current) > 0;

        private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>What the user asked to add.</summary>
    public sealed class PackageRequest
    {
        public PackageRequest(string name, string? version = null, PackageScope scope = PackageScope.Runtime, string? source = null)
        {
            Name = PackageArguments.ValidateName(name);
            Version = string.IsNullOrWhiteSpace(version) ? null : PackageArguments.ValidateVersion(version!);
            Scope = scope;
            Source = string.IsNullOrWhiteSpace(source) ? null : PackageArguments.ValidateSource(source!);
        }

        public string Name { get; }
        public string? Version { get; }
        public PackageScope Scope { get; }

        /// <summary>A URL or path for managers without a registry (Zig), or a non-default registry.</summary>
        public string? Source { get; }
    }

    /// <summary>A whole-file replacement of a manifest, applied before the action's commands run.</summary>
    public sealed class ManifestEdit
    {
        public ManifestEdit(string path, string text)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        public string Path { get; }
        public string Text { get; }
    }

    /// <summary>
    /// An add / remove / update as the steps to perform: manifest edits first (for managers that have no command
    /// for the change), then the manager's own commands in order. Nothing is executed by Core.
    /// </summary>
    public sealed class PackageAction
    {
        public PackageAction(string description, IEnumerable<ProcessStartInfo>? commands = null, IEnumerable<ManifestEdit>? edits = null)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Commands = (commands ?? Enumerable.Empty<ProcessStartInfo>()).ToList();
            Edits = (edits ?? Enumerable.Empty<ManifestEdit>()).ToList();
        }

        /// <summary>One line for the Output pane, e.g. <c>dart pub add http:^1.2.0</c>.</summary>
        public string Description { get; }
        public IReadOnlyList<ManifestEdit> Edits { get; }
        public IReadOnlyList<ProcessStartInfo> Commands { get; }
    }

    /// <summary>What a manager's process produced.</summary>
    public sealed class PackageToolOutput
    {
        public PackageToolOutput(int exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? "";
            StandardError = standardError ?? "";
        }

        public int ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
    }

    /// <summary>The manager reported a failure that should be shown to the user as is.</summary>
    public sealed class PackageManagerException : Exception
    {
        public PackageManagerException(string message) : base(message) { }
    }

    /// <summary>
    /// One language's own package manager. Implementations have no Visual Studio dependency and start nothing:
    /// they read manifests, build process start information and parse the manager's output.
    /// <c>tool</c> is the absolute path of the manager's executable (dart.exe, cargo.exe, conan.exe, zig.exe,
    /// wkg.exe) or, for Gradle, of the wrapper script.
    /// </summary>
    public interface IPackageManager
    {
        PackageEcosystem Ecosystem { get; }

        /// <summary>Name shown in Solution Explorer and messages: <c>pub</c>, <c>Cargo</c>, <c>Gradle</c>, <c>Conan</c>, <c>Zig</c>, <c>wkg</c>.</summary>
        string DisplayName { get; }

        /// <summary>Executable names to look for on PATH, most specific first, without extension.</summary>
        IReadOnlyList<string> ToolNames { get; }

        /// <summary>Manifest file names this manager owns, most specific first.</summary>
        IReadOnlyList<string> ManifestNames { get; }

        /// <summary>
        /// Declared direct packages, read from the manifest text alone: no process, no network. Entries carry
        /// <see cref="PackageEntry.Current"/> when a lock file next to the manifest states it.
        /// Throws <see cref="PackageManagerException"/> when the manifest cannot be understood.
        /// </summary>
        IReadOnlyList<PackageEntry> ReadManifest(string manifestPath);

        /// <summary>
        /// The commands whose output states current and newer versions of <paramref name="declared"/>: usually one,
        /// one per package for managers without a registry (Zig asks each dependency's repository), none when the
        /// manager cannot tell. They only read; none changes the manifest or the lock file.
        /// </summary>
        IReadOnlyList<ProcessStartInfo> CreateOutdated(string tool, string manifestPath, IReadOnlyList<PackageEntry> declared);

        /// <summary>
        /// Merges the outputs of the <see cref="CreateOutdated"/> commands (same order) into
        /// <paramref name="declared"/> (same order, same count). A failed run of a per-package command leaves that
        /// package without versions; throws <see cref="PackageManagerException"/> with the manager's message when
        /// the report as a whole failed.
        /// </summary>
        IReadOnlyList<PackageEntry> ApplyOutdated(IReadOnlyList<PackageEntry> declared, IReadOnlyList<PackageToolOutput> outputs);

        PackageAction Add(string tool, string manifestPath, PackageRequest request);

        PackageAction Remove(string tool, string manifestPath, PackageEntry package);

        /// <summary>
        /// Updates one package, or all when <paramref name="package"/> is null. <paramref name="toLatest"/> false
        /// stays inside the declared constraints (manifest unchanged where the manager supports that); true moves
        /// to the newest version and rewrites the constraint.
        /// </summary>
        PackageAction Update(string tool, string manifestPath, PackageEntry? package, bool toLatest);
    }
}
