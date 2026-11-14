using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Vistudio.Core.Mise
{
    /// <summary>
    /// Mise settings (Tools &gt; Options &gt; Mise). Stored in their own file, independent of the Kotlin and Zig settings.
    /// </summary>
    public sealed class MiseSettings
    {
        /// <summary>Absolute path to mise.exe (or its folder). Empty searches PATH.</summary>
        public string MisePath { get; set; } = "";
        /// <summary>Check the workspace's mise tools when a folder or solution opens and show an info bar when something needs attention.</summary>
        public bool InfoBar { get; set; } = true;
        /// <summary>End-of-line hints on tool lines of mise config files.</summary>
        public bool InlineHints { get; set; } = true;
        /// <summary>Also show the installed version on tool lines that are up to date.</summary>
        public bool ShowUpToDate { get; set; } = true;
        /// <summary>Status and upgrade lenses above the <c>[tools]</c> table.</summary>
        public bool CodeLens { get; set; } = true;
        /// <summary>How long an update check (<c>mise outdated</c>, which queries remote versions) is reused before it runs again.</summary>
        public int CacheMinutes { get; set; } = 30;

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vistudio", "mise-settings.xml");

        public static MiseSettings Load() => Load(FilePath);

        public static MiseSettings Load(string path)
        {
            if (!File.Exists(path)) return new MiseSettings();
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return (MiseSettings)new XmlSerializer(typeof(MiseSettings)).Deserialize(reader);
            }
            catch (InvalidOperationException) { return new MiseSettings(); }
            catch (XmlException) { return new MiseSettings(); }
        }

        public void Save() => Save(FilePath);

        public void Save(string path)
        {
            if (CacheMinutes < 1) CacheMinutes = 1;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new XmlSerializer(typeof(MiseSettings)).Serialize(writer, this);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
