using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Vistudio.Core
{
    /// <summary>
    /// The XML files under <c>%LOCALAPPDATA%\Vistudio</c> that hold Tools &gt; Options values. Writes go through a temporary
    /// file and a replace, so a crash while saving never leaves a half-written settings file behind.
    /// </summary>
    public static class SettingsXml
    {
        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        public static string PathFor(string fileName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", fileName);

        /// <summary>A missing file gives the defaults; an unreadable one throws (<see cref="InvalidOperationException"/>, <see cref="XmlException"/>).</summary>
        public static T Read<T>(string path) where T : new()
        {
            if (!File.Exists(path)) return new T();
            using (var reader = XmlReader.Create(path, ReaderSettings))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }

        /// <summary>A missing or unreadable file gives the defaults: a damaged settings file must not stop the editor.</summary>
        public static T ReadOrDefault<T>(string path) where T : new()
        {
            try { return Read<T>(path); }
            catch (InvalidOperationException) { return new T(); }
            catch (XmlException) { return new T(); }
        }

        public static void Write<T>(T value, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new XmlSerializer(typeof(T)).Serialize(writer, value);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    /// <summary>Non-generic view of <see cref="StoredSettings{TSelf}"/> for <see cref="WorkspaceSettings"/>.</summary>
    public abstract class StoredSettings
    {
        private protected StoredSettings(string fileName, string? workspacePrefix)
        {
            FileName = fileName;
            WorkspacePrefix = workspacePrefix;
        }

        // Not public: XmlSerializer would write public properties, and workspace keys could name them.
        internal string FileName { get; }
        /// <summary><c>vistudio.&lt;language&gt;.</c> keys of <c>.vs\settings.json</c> that override these settings; null when none do.</summary>
        internal string? WorkspacePrefix { get; }

        /// <summary>Throws <see cref="ArgumentException"/> for values Tools &gt; Options and workspace files must reject.</summary>
        public virtual void Validate() { }
    }

    /// <summary>
    /// One language's Tools &gt; Options settings in its own XML file, overridden per window by its <c>vistudio.&lt;language&gt;.*</c>
    /// keys in the workspace's <c>.vs\settings.json</c>. The serialized format is the derived class alone (the base has no
    /// public instance properties), so files written before this base existed still load.
    /// </summary>
    public abstract class StoredSettings<TSelf> : StoredSettings where TSelf : StoredSettings<TSelf>, new()
    {
        protected StoredSettings(string fileName, string? workspacePrefix) : base(fileName, workspacePrefix) { }

        public static string FilePath => SettingsXml.PathFor(new TSelf().FileName);

        /// <summary>Tools &gt; Options values only. Use this to edit and <see cref="Save()"/> the global defaults.</summary>
        public static TSelf LoadGlobal() => Load(FilePath);

        /// <summary>Effective settings of this Visual Studio window: global values overridden by its workspace's <c>.vs\settings.json</c>.</summary>
        public static TSelf Load() => LoadForWorkspace(WorkspaceSettings.CurrentRoot);

        public static TSelf LoadForWorkspace(string? root)
        {
            var settings = LoadGlobal();
            WorkspaceSettings.ApplyTo(settings, root);
            return settings;
        }

        public static TSelf Load(string path) => SettingsXml.ReadOrDefault<TSelf>(path);

        /// <summary>Runs before every save; validates by default. Override to also correct values (clamping) first.</summary>
        protected virtual void PrepareSave() => Validate();

        public void Save() => Save(FilePath);

        public void Save(string path)
        {
            PrepareSave();
            SettingsXml.Write((TSelf)this, path);
        }
    }
}
