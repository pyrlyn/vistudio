using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Vistudio.Core;

internal static class ScrollBarMapTests
{
    internal static void Run(Action<bool, string> check)
    {
        var defaults = new KotlinSettings();
        check(defaults.ScrollBarMode == ScrollBarMode.Inherit && defaults.ScrollBarMapWidth == ScrollBarMapWidth.Medium && defaults.ScrollBarMapPreview, "Scroll bar defaults inherit Visual Studio");
        check(ScrollBarMap.Resolve(defaults).Count == 0, "Inherit sets no local editor options");

        var bar = ScrollBarMap.Resolve(new KotlinSettings { ScrollBarMode = ScrollBarMode.Bar });
        check(bar.Count == 1 && Equals(bar[ScrollBarMap.MapModeOption], false), "Bar mode only turns map mode off; map-only options stay inherited");

        var map = ScrollBarMap.Resolve(new KotlinSettings { ScrollBarMode = ScrollBarMode.Map, ScrollBarMapWidth = ScrollBarMapWidth.Wide, ScrollBarMapPreview = false });
        check(Equals(map[ScrollBarMap.MapModeOption], true) && Equals(map[ScrollBarMap.SourceImageOption], true) && Equals(map[ScrollBarMap.WidthOption], 83.0) && Equals(map[ScrollBarMap.PreviewOption], false), "Map mode sets the code image, width and preview");
        check(map.Keys.All(ScrollBarMap.OptionNames.Contains), "Resolved options are all clearable");

        var off = ScrollBarMap.Resolve(new KotlinSettings { ScrollBarMode = ScrollBarMode.Map, ScrollBarMapWidth = ScrollBarMapWidth.Off });
        check(Equals(off[ScrollBarMap.MapModeOption], true) && Equals(off[ScrollBarMap.SourceImageOption], false) && !off.ContainsKey(ScrollBarMap.WidthOption), "Map width Off keeps map marks without the code image");

        check(ScrollBarMap.Pixels(ScrollBarMapWidth.Off) == 0 && ScrollBarMap.Pixels(ScrollBarMapWidth.Narrow) == 33 && ScrollBarMap.Pixels(ScrollBarMapWidth.Medium) == 58 && ScrollBarMap.Pixels(ScrollBarMapWidth.Wide) == 83, "Map widths match Visual Studio");

        var serializer = new XmlSerializer(typeof(KotlinSettings));
        var writer = new StringWriter();
        serializer.Serialize(writer, new KotlinSettings { ScrollBarMode = ScrollBarMode.Map, ScrollBarMapWidth = ScrollBarMapWidth.Narrow, ScrollBarMapPreview = false });
        var restored = (KotlinSettings)serializer.Deserialize(new StringReader(writer.ToString()));
        check(restored.ScrollBarMode == ScrollBarMode.Map && restored.ScrollBarMapWidth == ScrollBarMapWidth.Narrow && !restored.ScrollBarMapPreview, "Scroll bar settings survive the settings XML round trip");
        var legacy = (KotlinSettings)serializer.Deserialize(new StringReader("<KotlinSettings />"));
        check(legacy.ScrollBarMode == ScrollBarMode.Inherit, "Settings files without scroll bar entries inherit");
    }
}
