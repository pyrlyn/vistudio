using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vistudio.Core;
using Vistudio.Core.Projects;

namespace Vistudio
{
    /// <summary>
    /// Properties window (F4) view of a .kotlinproj project node. Editable values are validated by
    /// <see cref="KotlinProjectFile.Set"/> / <see cref="KotlinProjectFile.SetJdk"/> and saved atomically; the project's file
    /// watcher then refreshes the tree. Invalid input is rejected by the property grid with the validation message.
    /// </summary>
    [ComVisible(true)]
    public sealed class KotlinProjectProperties : CustomTypeDescriptor
    {
        private readonly string filePath;
        private readonly Action<string> log;
        private readonly Action saved;
        private KotlinProjectFile current;

        internal KotlinProjectProperties(KotlinProjectFile metadata, Action<string> log, Action saved)
        {
            filePath = metadata.FilePath;
            current = metadata;
            this.log = log;
            this.saved = saved;
        }

        private sealed class Definition
        {
            public Definition(string name, string category, string description, Func<KotlinProjectFile, string> read, string? editable = null, bool jdk = false, bool startup = false)
            { Name = name; Category = category; Description = description; Read = read; Editable = editable; Jdk = jdk; Startup = startup; }
            public bool Startup { get; }
            public string Name { get; }
            public string Category { get; }
            public string Description { get; }
            public Func<KotlinProjectFile, string> Read { get; }
            /// <summary>The Vistudio property element written by <see cref="KotlinProjectFile.Set"/>; null for read-only values.</summary>
            public string? Editable { get; }
            public bool Jdk { get; }
        }

        private static Definition Edit(string name, string category, string element, string description) =>
            new Definition(name, category, description, p => p.Get(element), element);

        private static readonly Definition[] Definitions =
        {
            new Definition("Gradle JDK", "Gradle", "JDK home used for Gradle builds, imports and debugging. Choose a detected installation or type a JDK home; empty uses Tools > Options > Kotlin / JAVA_HOME.",
                p => p.JdkHome, "JdkHome", jdk: true),
            new Definition("Gradle JDK version", "Gradle", "Version read from the selected JDK's release file.", p => p.JdkVersion),
            Edit("Gradle root", "Gradle", "GradleRoot", "Folder with gradlew.bat, relative to the project file."),
            Edit("Build tasks", "Gradle", "BuildTasks", "Gradle tasks for Build and F5, separated by spaces (for example build or :app:classes)."),
            Edit("Test tasks", "Gradle", "TestTasks", "Gradle tasks for Run Tests with Gradle."),
            Edit("Run tasks", "Gradle", "RunTasks", "Gradle tasks for Run with Gradle. A startup target with a run task replaces them."),
            new Definition("Startup target", "Targets", "Kotlin Multiplatform target for Run with Gradle, F5 and Ctrl+F5, for example :jvm or :app:js." +
                "JVM targets debug with F5; other targets run their Gradle run task with Ctrl+F5. Empty keeps RunTasks and the JVM launch.",
                p => p.Get("StartupTarget"), "StartupTarget", startup: true),
            new Definition("Project kind", "Targets", "JVM, Android, JS, Wasm, Native or Multiplatform, from the last Gradle import.", p => p.ProjectKind),
            new Definition("Targets", "Targets", "Imported Kotlin Multiplatform targets.", p => p.Imported("Targets").Replace(" ", ", ")),
            Edit("Import configurations", "Gradle", "ImportConfigurations", "Extra Gradle configurations to resolve on import, separated by spaces."),
            Edit("Main class", "Debug", "DebugMainClass", "JVM entry class for F5 / Ctrl+F5, for example sample.MainKt. Empty detects the single top-level main."),
            Edit("Source file", "Debug", "DebugSourceFile", "Kotlin file that declares the entry point, relative to the project file."),
            Edit("Arguments", "Debug", "DebugArguments", "Program arguments separated by spaces; use \"...\" to group."),
            Edit("Working directory", "Debug", "DebugWorkingDirectory", "Program working directory relative to the project file; empty uses the Gradle root."),
            new Definition("Project file", "Project", "Path of the .kotlinproj metadata file.", p => p.FilePath),
            new Definition("Project name", "Project", "Name shown in Solution Explorer.", p => p.Name),
            new Definition("Project GUID", "Project", "Identity used by solution files.", p => p.Get("ProjectGuid")),
            new Definition("Imported at (UTC)", "Last import", "Time of the last successful Gradle model import.", p => p.ImportedAtUtc),
            new Definition("Gradle version", "Last import", "Gradle version that produced the imported model.", p => p.Imported("GradleVersion")),
            new Definition("Gradle JVM", "Last import", "Java version and home that ran the import.", p =>
                p.Imported("JavaVersion").Length == 0 ? "" : p.Imported("JavaVersion") + " — " + p.Imported("JavaHome")),
        };

        public override string GetClassName() => "Kotlin Project";
        public override string GetComponentName() => current.Name;
        public override PropertyDescriptorCollection GetProperties() => GetProperties(null);
        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) =>
            new PropertyDescriptorCollection(Definitions.Select(d => (PropertyDescriptor)new Value(this, d)).ToArray(), true);
        public override object GetPropertyOwner(PropertyDescriptor? pd) => this;

        private void Write(Definition definition, string value)
        {
            // Edit the file as it is on disk now; concurrent external edits fail the save instead of being overwritten.
            var metadata = KotlinProjectFile.Load(filePath);
            if (definition.Jdk)
            {
                metadata.SetJdk(value ?? "");
                metadata.RefreshTools();
            }
            else metadata.Set(definition.Editable!, value ?? "");
            metadata.Save();
            current = KotlinProjectFile.Load(filePath);
            log("Kotlin project: " + definition.Name + " = " + (value ?? "") + " (" + filePath + ")");
            saved();
        }

        private string[] DetectedJdks()
        {
            try
            {
                var selected = current.JdkHome.Length != 0 ? current.ResolvePath(current.JdkHome) : KotlinSettings.Load().GradleJavaHome;
                return new[] { "" }.Concat(JdkInstallation.Discover(selected, log).Select(j => j.Home)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                log("Kotlin project: JDK discovery failed: " + e.Message);
                return new[] { "" };
            }
        }

        private sealed class Value : PropertyDescriptor
        {
            private readonly KotlinProjectProperties owner;
            private readonly Definition definition;

            public Value(KotlinProjectProperties owner, Definition definition)
                : base(definition.Name, new Attribute[] { new CategoryAttribute(definition.Category), new DescriptionAttribute(definition.Description) })
            { this.owner = owner; this.definition = definition; }

            public override Type ComponentType => typeof(KotlinProjectProperties);
            public override bool IsReadOnly => definition.Editable == null;
            public override Type PropertyType => typeof(string);
            public override TypeConverter Converter => definition.Jdk ? new JdkConverter(owner)
                : definition.Startup ? new TargetConverter(owner) : base.Converter;
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

        /// <summary>Drop-down of detected JDK homes that still accepts any typed path.</summary>
        private sealed class JdkConverter : StringConverter
        {
            private readonly KotlinProjectProperties owner;
            public JdkConverter(KotlinProjectProperties owner) => this.owner = owner;
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new StandardValuesCollection(owner.DetectedJdks());
            public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => value as string ?? base.ConvertFrom(context, culture, value);
        }

        /// <summary>Drop-down of imported Kotlin target ids plus empty (no startup target).</summary>
        private sealed class TargetConverter : StringConverter
        {
            private readonly KotlinProjectProperties owner;
            public TargetConverter(KotlinProjectProperties owner) => this.owner = owner;
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
                new StandardValuesCollection(new[] { "" }.Concat(KotlinTarget.All(owner.current).Select(t => t.Id)).ToArray());
            public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => value as string ?? base.ConvertFrom(context, culture, value);
        }
    }
}
