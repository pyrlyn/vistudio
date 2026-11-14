using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vistudio.Core.Languages;
using Vistudio.Core.Packages;

/// <summary>The language catalog against the shipped icon manifest and file associations (T58).</summary>
internal static class LanguageTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(LanguageCatalog.All.Count == 8, "Eight languages");
        check(LanguageCatalog.All.Select(l => l.Id).Distinct().Count() == 8, "Language ids are unique");
        var extensions = LanguageCatalog.All.SelectMany(l => l.Extensions).ToList();
        check(extensions.Distinct().Count() == extensions.Count, "No extension belongs to two languages");
        check(extensions.All(e => e.StartsWith(".") && e == e.ToLowerInvariant()), "Extensions are lower case with a dot");
        check(LanguageCatalog.ForFile(@"C:\x\main.RS") == LanguageCatalog.Rust, "Extension lookup ignores case");
        check(LanguageCatalog.ForFile(@"C:\x\lib.h") == LanguageCatalog.Cpp, "Headers belong to C++");
        check(LanguageCatalog.ForFile(@"C:\x\main.c") == LanguageCatalog.C, ".c is C");
        check(LanguageCatalog.ForFile(@"C:\x\build.zig.zon") == LanguageCatalog.Zig, ".zon is Zig");
        check(LanguageCatalog.ForFile(@"C:\x\readme.md") == null, "Unknown extension");
        check(LanguageCatalog.ForProjectFile(@"C:\x\app.CPPPROJ") == LanguageCatalog.Cpp, "Project extension lookup");
        check(LanguageCatalog.ById("WASM") == LanguageCatalog.Wasm, "Id lookup ignores case");
        check(LanguageCatalog.C.EditorFromVisualStudio && LanguageCatalog.Cpp.EditorFromVisualStudio && !LanguageCatalog.Rust.EditorFromVisualStudio,
            "Only C and C++ keep Visual Studio's own editor services");
        check(LanguageCatalog.Kotlin.Packages == PackageEcosystem.Gradle && LanguageCatalog.Java.Packages == PackageEcosystem.Gradle
            && LanguageCatalog.C.Packages == PackageEcosystem.Conan && LanguageCatalog.Wasm.Packages == PackageEcosystem.Wkg,
            "Package managers follow the creator's choices");

        var manifest = XDocument.Load(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Icons", "Languages.imagemanifest")));
        XNamespace ns = "http://schemas.microsoft.com/VisualStudio/ImageManifestSchema/2014";
        var images = manifest.Descendants(ns + "Image").ToList();
        check(images.All(i => Guid.Parse((string)i.Attribute("Guid")!) == LanguageCatalog.IconGuid), "Image manifest uses the catalog's icon guid");
        var ids = images.Select(i => (int)i.Attribute("ID")!).ToList();
        var registration = File.ReadAllText(DartTests.FindRepositoryFile(Path.Combine("src", "Vistudio.Vsix", "Registration", "LanguageFiles.pkgdef")));
        foreach (var language in LanguageCatalog.All.Where(l => l.IconId != 0))
        {
            var moniker = "\"" + LanguageCatalog.IconGuid.ToString("D") + ":" + language.IconId + "\"";
            check(ids.Contains(language.IconId), language.DisplayName + " icon id exists in the image manifest");
            check(registration.Contains("ShellFileAssociations\\" + language.ProjectExtension + "]\r\n\"DefaultIconMoniker\"=" + moniker),
                language.DisplayName + " project files show the language icon");
            if (!language.EditorFromVisualStudio)
                foreach (var extension in language.Extensions)
                    check(registration.Contains("ShellFileAssociations\\" + extension + "]"), extension + " has a file icon");
        }
        // Visual C++ already maps .c/.cpp/.h; overriding it would replace icons other C++ tooling relies on.
        check(!registration.Contains("ShellFileAssociations\\.cpp]") && !registration.Contains("ShellFileAssociations\\.c]"), "C and C++ files keep Visual Studio's icons");
    }
}
