using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vistudio.Core.Dart.Preview
{
    /// <summary>A named device size in logical pixels.</summary>
    public sealed class DevicePreset
    {
        public DevicePreset(string name, int width, int height) { Name = name; Width = width; Height = height; }
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public override string ToString() => Name + " (" + Width + "×" + Height + ")";
    }

    public enum PreviewTheme { Ide, Light, Dark }

    /// <summary>
    /// One rendering of the previewer: the surface size (null = fill the window), theme, text scale and locale. The
    /// window applies it to its WebView2 instance: the surface size becomes the page viewport (MediaQuery size), the
    /// theme its <c>prefers-color-scheme</c> (the platform brightness of previews), the text scale the root font size
    /// (Flutter web's text scale factor) and the locale <c>navigator.languages</c> (the platform locales). Values given
    /// in <c>@Preview(...)</c> (size, brightness, textScaleFactor, localizations) take precedence inside the previewer.
    /// </summary>
    public sealed class PreviewConfiguration
    {
        public PreviewConfiguration(string label, int? width, int? height, PreviewTheme theme, double textScale, string? locale)
        { Label = label; Width = width; Height = height; Theme = theme; TextScale = textScale; Locale = locale; }
        public string Label { get; }
        public int? Width { get; }
        public int? Height { get; }
        public PreviewTheme Theme { get; }
        public double TextScale { get; }
        /// <summary>BCP 47 tag, or null for the system languages.</summary>
        public string? Locale { get; }

        /// <summary>The effective color scheme, <c>dark</c> or <c>light</c> (the window emulates it per page with DevTools media emulation).</summary>
        public string ProfileKey(bool ideIsDark) => (Theme == PreviewTheme.Dark || Theme == PreviewTheme.Ide && ideIsDark) ? "dark" : "light";

        /// <summary>
        /// The script WebView2 runs before every document: sets the root font size (Flutter web reads its text scale
        /// factor from it at startup) and, with a locale, overrides <c>navigator.languages</c> (Flutter's platform locales).
        /// </summary>
        public string DocumentScript() => Script(live: false);

        /// <summary>
        /// The same settings applied to a running page without reloading it: Flutter web recomputes the text scale when
        /// the root font size changes (a resize observer) and the locales on <c>languagechange</c>. Reloading a page is
        /// avoided because the previewer's debug service keeps counting every page instance it saw.
        /// </summary>
        public string LiveScript() => Script(live: true);

        private string Script(bool live)
        {
            var script = new StringBuilder("(function(){");
            script.Append("var size='").Append((16 * TextScale).ToString("0.###", CultureInfo.InvariantCulture)).Append("px';");
            script.Append("function apply(){var e=document.documentElement;if(e)e.style.fontSize=size;}");
            script.Append("apply();");
            if (!live) script.Append("document.addEventListener('readystatechange',apply);");
            // The browser's own getters are kept, so "System" can restore them later.
            script.Append("var p=Navigator.prototype;if(!window.__vistudioLanguages)window.__vistudioLanguages={languages:Object.getOwnPropertyDescriptor(p,'languages'),language:Object.getOwnPropertyDescriptor(p,'language')};");
            script.Append("try{");
            if (Locale != null)
            {
                script.Append("var langs=['").Append(Locale).Append("'];");
                script.Append("Object.defineProperty(p,'languages',{get:function(){return langs;},configurable:true});");
                script.Append("Object.defineProperty(p,'language',{get:function(){return langs[0];},configurable:true});");
            }
            else
            {
                script.Append("var o=window.__vistudioLanguages;if(o.languages)Object.defineProperty(p,'languages',o.languages);if(o.language)Object.defineProperty(p,'language',o.language);");
            }
            script.Append("}catch(e){}");
            if (live) script.Append("window.dispatchEvent(new Event('languagechange'));");
            return script.Append("})();").ToString();
        }

        public override string ToString() => Label;
    }

    /// <summary>The choices of the preview window toolbar; persisted in <see cref="PreviewWindowState"/>.</summary>
    public sealed class PreviewSelection
    {
        /// <summary>Device choice value: fill the window.</summary>
        public const string Fit = "fit";
        /// <summary>Device choice value: <see cref="PreviewWindowState.CustomWidth"/> x <see cref="PreviewWindowState.CustomHeight"/>.</summary>
        public const string Custom = "custom";
        /// <summary>Choice value of any dimension: every configured value side by side.</summary>
        public const string Compare = "compare";
        /// <summary>Theme choice value: light and dark side by side.</summary>
        public const string BothThemes = "both";
        /// <summary>Locale choice value: the system languages.</summary>
        public const string SystemLocale = "";

        public string Device { get; set; } = Fit;
        public int CustomWidth { get; set; } = 400;
        public int CustomHeight { get; set; } = 800;
        /// <summary><c>ide</c>, <c>light</c>, <c>dark</c> or <see cref="BothThemes"/>.</summary>
        public string Theme { get; set; } = "ide";
        /// <summary>A factor such as <c>1.3</c>, or <see cref="Compare"/>.</summary>
        public string TextScale { get; set; } = "1.0";
        /// <summary>A BCP 47 tag, <see cref="SystemLocale"/> or <see cref="Compare"/>.</summary>
        public string Locale { get; set; } = SystemLocale;
    }

    /// <summary>Parsing of the configuration settings and expansion of a selection into the configurations to show.</summary>
    public static class PreviewConfigurations
    {
        public const string DefaultDevices = "Phone=390x844; Tablet=820x1180; Desktop=1440x900";
        public const string DefaultTextScales = "1.0, 1.3, 2.0";
        public const string DefaultLocales = "en-US, de-DE, ar";
        /// <summary>At most this many previewer instances side by side; each one is a full Flutter web app.</summary>
        public const int MaximumSideBySide = 6;

        private static readonly Regex DeviceEntry = new Regex(@"^(?<name>[^=;]{1,40}?)\s*=\s*(?<w>\d{2,5})\s*[x×X*]\s*(?<h>\d{2,5})$", RegexOptions.CultureInvariant);
        private static readonly Regex LocaleTag = new Regex(@"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

        /// <summary>Parses <c>Name=WIDTHxHEIGHT; ...</c>; throws <see cref="ArgumentException"/> with the offending entry.</summary>
        public static IReadOnlyList<DevicePreset> ParseDevices(string? text)
        {
            var result = new List<DevicePreset>();
            foreach (var raw in (text ?? "").Split(';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                var match = DeviceEntry.Match(entry);
                if (!match.Success) throw new ArgumentException("Invalid device preset '" + entry + "'. Use Name=WIDTHxHEIGHT, for example Phone=390x844.");
                int width = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture), height = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
                if (width < 100 || height < 100 || width > 7680 || height > 7680) throw new ArgumentException("Device preset '" + entry + "' must be between 100 and 7680 pixels in each direction.");
                var name = match.Groups["name"].Value.Trim();
                if (name.Equals(PreviewSelection.Fit, StringComparison.OrdinalIgnoreCase) || name.Equals(PreviewSelection.Custom, StringComparison.OrdinalIgnoreCase) || name.Equals(PreviewSelection.Compare, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Device preset name '" + name + "' is reserved.");
                if (result.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Device preset '" + name + "' is defined twice.");
                result.Add(new DevicePreset(name, width, height));
            }
            return result;
        }

        /// <summary>Parses <c>1.0, 1.3, 2.0</c> (0.5-4.0); 1.0 is always included.</summary>
        public static IReadOnlyList<double> ParseTextScales(string? text)
        {
            var result = new List<double> { 1.0 };
            foreach (var raw in (text ?? "").Split(',', ';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                if (!double.TryParse(entry, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0.5 || value > 4.0)
                    throw new ArgumentException("Invalid text scale '" + entry + "'. Use numbers between 0.5 and 4.0 with a dot, for example 1.3.");
                if (!result.Any(v => Math.Abs(v - value) < 0.0001)) result.Add(Math.Round(value, 3));
            }
            return result.OrderBy(v => v).ToList();
        }

        /// <summary>Parses <c>en-US, de-DE, ar</c>; underscores become hyphens.</summary>
        public static IReadOnlyList<string> ParseLocales(string? text)
        {
            var result = new List<string>();
            foreach (var raw in (text ?? "").Split(',', ';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                if (!LocaleTag.IsMatch(entry)) throw new ArgumentException("Invalid locale '" + entry + "'. Use language tags such as en-US, de or ar.");
                var tag = entry.Replace('_', '-');
                if (!result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
            }
            return result;
        }

        public static string FormatScale(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

        /// <summary>
        /// The configurations for a selection: the product of the chosen devices, themes, text scales and locales, at most
        /// <see cref="MaximumSideBySide"/> (<paramref name="truncated"/> tells when more were requested). Unknown values
        /// (a preset removed from the settings) fall back to the defaults.
        /// </summary>
        public static IReadOnlyList<PreviewConfiguration> Expand(PreviewSelection selection, IReadOnlyList<DevicePreset> devices,
            IReadOnlyList<double> textScales, IReadOnlyList<string> locales, out bool truncated)
        {
            var sizes = new List<(string Label, int? Width, int? Height)>();
            if (selection.Device.Equals(PreviewSelection.Compare, StringComparison.OrdinalIgnoreCase) && devices.Count > 0)
                sizes.AddRange(devices.Select(d => (d.Name, (int?)d.Width, (int?)d.Height)));
            else if (selection.Device.Equals(PreviewSelection.Custom, StringComparison.OrdinalIgnoreCase))
            {
                int width = Clamp(selection.CustomWidth), height = Clamp(selection.CustomHeight);
                sizes.Add((width + "×" + height, width, height));
            }
            else if (devices.FirstOrDefault(d => d.Name.Equals(selection.Device, StringComparison.OrdinalIgnoreCase)) is DevicePreset preset)
                sizes.Add((preset.Name, preset.Width, preset.Height));
            else sizes.Add(("", null, null));

            var themes = selection.Theme.ToLowerInvariant() switch
            {
                "light" => new[] { PreviewTheme.Light },
                "dark" => new[] { PreviewTheme.Dark },
                PreviewSelection.BothThemes => new[] { PreviewTheme.Light, PreviewTheme.Dark },
                _ => new[] { PreviewTheme.Ide },
            };

            IReadOnlyList<double> scales;
            if (selection.TextScale.Equals(PreviewSelection.Compare, StringComparison.OrdinalIgnoreCase)) scales = textScales.Count == 0 ? new[] { 1.0 } : textScales;
            else scales = new[] { double.TryParse(selection.TextScale, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale >= 0.5 && scale <= 4.0 ? scale : 1.0 };

            IReadOnlyList<string?> chosenLocales;
            if (selection.Locale.Equals(PreviewSelection.Compare, StringComparison.OrdinalIgnoreCase)) chosenLocales = locales.Count == 0 ? new string?[] { null } : locales.Cast<string?>().ToList();
            else chosenLocales = new[] { string.IsNullOrWhiteSpace(selection.Locale) || !LocaleTag.IsMatch(selection.Locale.Trim()) ? null : selection.Locale.Trim().Replace('_', '-') };

            var result = new List<PreviewConfiguration>();
            truncated = false;
            foreach (var size in sizes)
                foreach (var theme in themes)
                    foreach (var textScale in scales)
                        foreach (var locale in chosenLocales)
                        {
                            if (result.Count == MaximumSideBySide) { truncated = true; return result; }
                            var parts = new List<string>();
                            if (size.Label.Length > 0) parts.Add(size.Label);
                            if (theme != PreviewTheme.Ide) parts.Add(theme == PreviewTheme.Dark ? "Dark" : "Light");
                            if (Math.Abs(textScale - 1.0) > 0.0001 || scales.Count > 1) parts.Add(FormatScale(textScale) + "×");
                            if (locale != null) parts.Add(locale);
                            result.Add(new PreviewConfiguration(parts.Count == 0 ? "Preview" : string.Join(" · ", parts), size.Width, size.Height, theme, textScale, locale));
                        }
            return result;
        }

        private static int Clamp(int value) => Math.Max(100, Math.Min(7680, value));
    }

    /// <summary>The preview window's toolbar choices, saved as soon as they change (not part of Tools &gt; Options).</summary>
    public sealed class PreviewWindowState : StoredSettings<PreviewWindowState>
    {
        public string Device { get; set; } = PreviewSelection.Fit;
        public int CustomWidth { get; set; } = 400;
        public int CustomHeight { get; set; } = 800;
        public string Theme { get; set; } = "ide";
        public string TextScale { get; set; } = "1.0";
        public string Locale { get; set; } = PreviewSelection.SystemLocale;
        /// <summary>The package root last previewed, so a solution with several Flutter packages reopens on it.</summary>
        public string LastProject { get; set; } = "";

        public PreviewWindowState() : base("flutter-preview-window.xml", null) { }

        public PreviewSelection ToSelection() => new PreviewSelection
        {
            Device = Device, CustomWidth = CustomWidth, CustomHeight = CustomHeight, Theme = Theme, TextScale = TextScale, Locale = Locale,
        };

        public void Apply(PreviewSelection selection)
        {
            Device = selection.Device; CustomWidth = selection.CustomWidth; CustomHeight = selection.CustomHeight;
            Theme = selection.Theme; TextScale = selection.TextScale; Locale = selection.Locale;
        }
    }
}
