using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Vistudio.Core.Languages;

namespace Vistudio.Languages
{
    /// <summary>
    /// Properties window (F4) view of a language project node. Values are validated by <see cref="LanguageProjectFile.Set"/>
    /// against the file as it is on disk and saved atomically; invalid input is rejected with the reason.
    /// </summary>
    [ComVisible(true)]
    public sealed class LanguageProjectProperties : ProjectBrowseObject
    {
        private readonly string filePath;
        private readonly Action<string> log;
        private readonly Action saved;
        private LanguageProjectFile current;

        internal LanguageProjectProperties(LanguageProjectFile metadata, Action<string> log, Action saved)
        {
            filePath = metadata.FilePath;
            current = metadata;
            this.log = log;
            this.saved = saved;
        }

        private sealed class Definition
        {
            public Definition(string name, string category, string description, Func<LanguageProjectFile, string> read, string? editable = null)
            { Name = name; Category = category; Description = description; Read = read; Editable = editable; }
            public string Name { get; }
            public string Category { get; }
            public string Description { get; }
            public Func<LanguageProjectFile, string> Read { get; }
            public string? Editable { get; }
        }

        private static Definition Edit(string name, string category, string element, string description) =>
            new Definition(name, category, description, p => p.Get(element), element);

        private const string Chain = " Commands run without a shell; chain several with &&. The program is looked up in the project folder, Tool folders, then PATH.";

        private static readonly Definition[] Definitions =
        {
            Edit("Build command", "Commands", "BuildCommand", "Runs for Build and before F5 when the solution builds the project." + Chain),
            Edit("Run command", "Commands", "RunCommand", "Runs for the project's Run command and for Ctrl+F5 when no debug program is set." + Chain),
            Edit("Test command", "Commands", "TestCommand", "Runs for Run Tests." + Chain),
            Edit("Clean command", "Commands", "CleanCommand", "Runs for Clean and before Rebuild; empty does nothing." + Chain),
            Edit("Debug program", "Debug", "DebugProgram", "Program F5 starts under the native debugger (Rust, Zig, C, C++), relative to the project file. Dart and Java use their own debuggers; empty runs the Run command."),
            Edit("Debug arguments", "Debug", "DebugArguments", "Program arguments for F5 and Ctrl+F5, separated by spaces; use \"...\" to group."),
            Edit("Working directory", "Debug", "WorkingDirectory", "Folder the commands and the program run in, relative to the project file."),
            Edit("Tool folders", "Tools", "ToolFolders", "Folders searched for the commands' programs before PATH, separated by ';' (for example a Rust toolchain or an SDK's bin folder)."),
            Edit("Package manager", "Tools", "PackageTool", "Full path of the package manager executable for the Packages node; empty finds it like the commands do."),
            Edit("Build system", "Build", "BuildSystem", "Native (the language's own tool) or Bazel (bazel build //..., bazel run, bazel test //...; bazelisk is used when bazel is not found). Switching replaces the commands that still hold the other system's defaults; edited commands stay."),
            Edit("Build cache", "Build", "BuildCache", "true turns on the build tool's own cache for every command: Bazel --disk_cache, Gradle --build-cache (Kotlin, Java), sccache for Cargo and CMake when installed. Zig, Dart/Flutter and wasm-tools cache by themselves. Flags already set on the command line, in .bazelrc or gradle.properties win."),
            Edit("Remote cache", "Build", "RemoteCache", "Optional shared cache URL: Bazel --remote_cache (http, https, grpc, grpcs) and a read-only Gradle HTTP build cache. Empty uses only the local cache."),
            new Definition("Build cache does", "Build", "What the build cache setting turns on for this project's tools.", p => BuildCache.Describe(p.Language, p.BuildSystem)),
            new Definition("Language", "Project", "The project's language, from the project file extension.", p => p.Language.DisplayName),
            new Definition("Project file", "Project", "Path of the project file.", p => p.FilePath),
            new Definition("Project name", "Project", "Name shown in Solution Explorer.", p => p.Name),
            new Definition("Project GUID", "Project", "Identity used by solution files.", p => p.ProjectGuid),
        };

        public override string GetClassName() => current.Language.DisplayName + " Project";
        public override string GetComponentName() => current.Name;
        public override PropertyDescriptorCollection GetProperties() => GetProperties(null);
        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) =>
            new PropertyDescriptorCollection(Definitions.Select(d => (PropertyDescriptor)new Value(this, d)).ToArray(), true);
        public override object GetPropertyOwner(PropertyDescriptor? pd) => this;

        private void Write(Definition definition, string value)
        {
            // Edit the file as it is on disk now, so a concurrent external edit is not overwritten by a stale copy.
            var metadata = LanguageProjectFile.Load(filePath);
            metadata.Set(definition.Editable!, value ?? "");
            current = LanguageProjectFile.Load(filePath);
            log(definition.Name + " = " + (value ?? "") + " (" + filePath + ")");
            saved();
        }

        private sealed class Value : PropertyDescriptor
        {
            private readonly LanguageProjectProperties owner;
            private readonly Definition definition;

            public Value(LanguageProjectProperties owner, Definition definition)
                : base(definition.Name, new Attribute[] { new CategoryAttribute(definition.Category), new DescriptionAttribute(definition.Description) })
            { this.owner = owner; this.definition = definition; }

            public override Type ComponentType => typeof(LanguageProjectProperties);
            public override bool IsReadOnly => definition.Editable == null;
            public override Type PropertyType => typeof(string);
            public override bool CanResetValue(object component) => false;
            public override object GetValue(object? component) => definition.Read(owner.current);
            public override void ResetValue(object component) { }
            public override bool ShouldSerializeValue(object component) => false;

            public override void SetValue(object? component, object? value)
            {
                if (IsReadOnly) throw new NotSupportedException(definition.Name + " is read-only.");
                owner.Write(definition, value as string ?? "");
                OnValueChanged(component, EventArgs.Empty);
            }
        }
    }
}
