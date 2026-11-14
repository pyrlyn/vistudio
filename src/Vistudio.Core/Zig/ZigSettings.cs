using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Vistudio.Core.Zig
{
    /// <summary>Which zig ast-check messages reach squiggles and the Error List.</summary>
    public enum ZigDiagnosticLevel
    {
        /// <summary>Errors only; notes are appended to the error text.</summary>
        Errors,
        /// <summary>Errors plus each note as its own Error List message at the note's location.</summary>
        ErrorsAndNotes,
    }

    /// <summary>
    /// Zig settings (Tools &gt; Options &gt; Zig). Stored next to, and independent of, the Kotlin settings so the two
    /// languages never overwrite each other.
    /// </summary>
    public sealed class ZigSettings
    {
        /// <summary>Absolute path to zig.exe. Empty searches PATH.</summary>
        public string ZigPath { get; set; } = "";
        /// <summary>Zig library folder (contains <c>std</c>) passed as <c>--zig-lib-dir</c>. Empty uses the one bundled with zig.exe.</summary>
        public string LibDirectory { get; set; } = "";
        /// <summary>Extra compiler flags for Build File and Run, for example <c>-O ReleaseSafe -lc</c>.</summary>
        public string DefaultFlags { get; set; } = "";
        /// <summary>Extra flags for <c>zig build</c>, for example <c>-Doptimize=ReleaseFast</c>.</summary>
        public string BuildFlags { get; set; } = "";
        /// <summary>Build step for Build Project. Empty runs the default step (install).</summary>
        public string BuildStep { get; set; } = "";
        public bool AstCheckOnSave { get; set; } = true;
        /// <summary>Also check the unsaved text after typing stops.</summary>
        public bool AstCheckWhileTyping { get; set; } = true;
        public int AstCheckDelayMilliseconds { get; set; } = 600;
        public ZigDiagnosticLevel DiagnosticLevel { get; set; } = ZigDiagnosticLevel.ErrorsAndNotes;
        /// <summary>Absolute path to a user-installed ZLS executable. Empty disables Zig IntelliSense and navigation.</summary>
        public string LanguageServerPath { get; set; } = "";
        public string LanguageServerArguments { get; set; } = "";
        /// <summary>Show ZLS's own diagnostics as well. Off while ast-check runs, so parse errors are not reported twice.</summary>
        public bool LanguageServerDiagnostics { get; set; }
        public bool CodeLens { get; set; } = true;

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "zig-settings.xml");

        /// <summary>Tools &gt; Options values only. Use this to edit and <see cref="Save()"/> the global defaults.</summary>
        public static ZigSettings LoadGlobal() => Load(FilePath);

        /// <summary>Effective settings of this Visual Studio window: global values overridden by its workspace's <c>.vs\settings.json</c>.</summary>
        public static ZigSettings Load() => LoadForWorkspace(WorkspaceSettings.CurrentRoot);

        public static ZigSettings LoadForWorkspace(string? root)
        {
            var settings = LoadGlobal();
            WorkspaceSettings.ApplyTo(settings, root);
            return settings;
        }

        public static ZigSettings Load(string path)
        {
            if (!File.Exists(path)) return new ZigSettings();
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return (ZigSettings)new XmlSerializer(typeof(ZigSettings)).Deserialize(reader);
            }
            catch (InvalidOperationException) { return new ZigSettings(); }
            catch (XmlException) { return new ZigSettings(); }
        }

        public void Save() => Save(FilePath);

        public void Save(string path)
        {
            ZigArguments.Split(DefaultFlags);
            ZigArguments.Split(BuildFlags);
            ZigArguments.Split(LanguageServerArguments);
            if (BuildStep.Length > 0) ZigArguments.ValidateStep(BuildStep);
            if (AstCheckDelayMilliseconds < 100) AstCheckDelayMilliseconds = 100;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new XmlSerializer(typeof(ZigSettings)).Serialize(writer, this);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
