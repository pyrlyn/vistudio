using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Vistudio.Core.Mise;

namespace Vistudio.Mise
{
    /// <summary>Tools &gt; Options &gt; Mise &gt; General. Persists to <see cref="MiseSettings.FilePath"/>.</summary>
    [ComVisible(true)]
    [Guid("5d2b8e4f-1a6c-4f39-b0e7-9c3a2d1f6e58")]
    public sealed class MiseOptionsPage : DialogPage
    {
        private MiseSettings settings = MiseSettings.Load();

        [Category("mise"), DisplayName("mise.exe path"), Description("Absolute path to mise.exe (or its folder). Empty searches PATH and the default install folders. mise is not bundled; install it from https://mise.jdx.dev.")]
        public string MisePath { get => settings.MisePath; set => settings.MisePath = value; }
        [Category("mise"), DisplayName("Update check cache (minutes)"), Description("How long an update check (mise outdated, which queries remote versions) is reused. Saving a mise config, Check Tools and every action check again. Minimum 1.")]
        public int CacheMinutes { get => settings.CacheMinutes; set => settings.CacheMinutes = value; }
        [Category("Workspace"), DisplayName("Info bar"), Description("Check the workspace's mise tools when a folder or solution opens and show an info bar when tools are missing, updates exist or the config is not trusted.")]
        public bool InfoBar { get => settings.InfoBar; set => settings.InfoBar = value; }
        [Category("Editor"), DisplayName("Inline hints"), Description("Show 'not installed', 'current → in range · latest' or the installed version after each tool line of mise.toml, .tool-versions and the other mise configs.")]
        public bool InlineHints { get => settings.InlineHints; set => settings.InlineHints = value; }
        [Category("Editor"), DisplayName("Show up-to-date versions"), Description("Also show the installed version on tool lines that need nothing.")]
        public bool ShowUpToDate { get => settings.ShowUpToDate; set => settings.ShowUpToDate = value; }
        [Category("Editor"), DisplayName("CodeLens"), Description("Status, Upgrade all and Upgrade all to latest above the [tools] table. Reopen editors after changing.")]
        public bool CodeLens { get => settings.CodeLens; set => settings.CodeLens = value; }

        public override void LoadSettingsFromStorage() => settings = MiseSettings.Load();

        public override void SaveSettingsToStorage()
        {
            settings.Save();
            MiseService.OnSettingsChanged();
        }

        public override void ResetSettings() => settings = new MiseSettings();
    }
}
