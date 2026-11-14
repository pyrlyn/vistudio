using System.Collections.Generic;

namespace Vistudio.Core
{
    public enum ScrollBarMode { Inherit, Bar, Map }

    public enum ScrollBarMapWidth { Off, Narrow, Medium, Wide }

    /// <summary>
    /// Maps Vistudio scroll bar settings to Visual Studio editor option names
    /// (<c>DefaultTextViewHostOptions</c>) applied locally to Kotlin and Java views.
    /// </summary>
    public static class ScrollBarMap
    {
        /// <summary>Visual Studio's "Use map mode for vertical scroll bar" (false is bar mode).</summary>
        public const string MapModeOption = "OverviewMargin/ShowEnhancedScrollBar";
        /// <summary>The code image inside the map; Visual Studio's "Source overview: Off" hides it.</summary>
        public const string SourceImageOption = "OverviewMargin/ShowSourceImageMargin";
        public const string WidthOption = "OverviewMargin/SourceImageMarginWidth";
        public const string PreviewOption = "OverviewMargin/ShowPreview";

        public static readonly IReadOnlyList<string> OptionNames = new[] { MapModeOption, SourceImageOption, WidthOption, PreviewOption };

        /// <summary>Pixel widths from Visual Studio's own "Source overview" setting.</summary>
        public static double Pixels(ScrollBarMapWidth width) =>
            width == ScrollBarMapWidth.Narrow ? 33.0 : width == ScrollBarMapWidth.Wide ? 83.0 : width == ScrollBarMapWidth.Off ? 0.0 : 58.0;

        /// <summary>Options to set on the view; every other name in <see cref="OptionNames"/> is cleared (inherited).</summary>
        public static IReadOnlyDictionary<string, object> Resolve(KotlinSettings settings)
        {
            var options = new Dictionary<string, object>();
            switch (settings.ScrollBarMode)
            {
                case ScrollBarMode.Bar:
                    options[MapModeOption] = false;
                    break;
                case ScrollBarMode.Map:
                    options[MapModeOption] = true;
                    options[SourceImageOption] = settings.ScrollBarMapWidth != ScrollBarMapWidth.Off;
                    if (settings.ScrollBarMapWidth != ScrollBarMapWidth.Off) options[WidthOption] = Pixels(settings.ScrollBarMapWidth);
                    options[PreviewOption] = settings.ScrollBarMapPreview;
                    break;
            }
            return options;
        }
    }
}
