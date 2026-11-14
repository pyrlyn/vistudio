namespace Vistudio.Core.Mise
{
    /// <summary>
    /// Mise settings (Tools &gt; Options &gt; Mise). Stored in their own file, independent of the Kotlin and Zig settings.
    /// No workspace overrides: <see cref="StoredSettings{TSelf}.Load()"/> returns the global values.
    /// </summary>
    public sealed class MiseSettings : StoredSettings<MiseSettings>
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

        public MiseSettings() : base("mise-settings.xml", null) { }

        protected override void PrepareSave()
        {
            if (CacheMinutes < 1) CacheMinutes = 1;
        }
    }
}
