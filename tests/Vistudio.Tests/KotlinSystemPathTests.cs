using System;
using System.IO;
using Vistudio.Core;

internal static class KotlinSystemPathTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        check(KotlinSystemPath.Find("--stdio --system-path=\"C:\\a b\\cache\"") == "C:\\a b\\cache", "Quoted --system-path= is found");
        check(KotlinSystemPath.Find("--system-path=C:\\cache --stdio") == "C:\\cache", "Unquoted --system-path= is found");
        check(KotlinSystemPath.Find("--stdio --system-path C:\\cache") == "C:\\cache", "Space-separated --system-path is found");
        check(KotlinSystemPath.Find("--stdio") == null && KotlinSystemPath.Find("--stdio --old-system-path=x") == null, "Other arguments are not a system path");
        check(KotlinSystemPath.WithPath("--stdio --system-path=\"C:\\a b\\cache\" --x", "D:\\c-2") == "--stdio --system-path=\"D:\\c-2\" --x", "The system path is replaced in place");
        check(KotlinSystemPath.WithPath("--stdio", "D:\\c") == "--stdio --system-path=\"D:\\c\"", "A missing system path is appended");
        check(KotlinSystemPath.Slot("C:\\cache\\", 1) == "C:\\cache\\" && KotlinSystemPath.Slot("C:\\cache\\", 3) == "C:\\cache-3", "Slot 1 is the configured path, later slots get a suffix");

        var server = Path.Combine(root, "bin", "intellij-server.exe");
        var basePath = Path.Combine(root, "kotlin cache");
        var arguments = "--stdio --system-path=\"" + basePath + "\"";
        using (var first = KotlinSystemPath.Acquire(server, arguments, Path.Combine(root, "default")))
        {
            check(first.Slot == 1 && first.SystemPath == basePath && first.Arguments == arguments, "The first instance gets the configured path");
            using (var second = KotlinSystemPath.Acquire(server, arguments, Path.Combine(root, "default")))
            {
                check(second.Slot == 2 && second.SystemPath == basePath + "-2" && second.Arguments == "--stdio --system-path=\"" + basePath + "-2\"",
                    "A second instance gets its own path");
                using (var third = KotlinSystemPath.Acquire(server, arguments, Path.Combine(root, "default")))
                    check(third.Slot == 3, "A third instance gets the next path");
            }
            using (var again = KotlinSystemPath.Acquire(server, arguments, Path.Combine(root, "default")))
                check(again.Slot == 2, "A released slot is reused, keeping its cache");
        }
        using (var reused = KotlinSystemPath.Acquire(server, arguments, Path.Combine(root, "default")))
            check(reused.Slot == 1, "The configured path is free again after its lease ends");

        // A server started without Vistudio's lock still holds its own .app.lock.
        var foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(foreign);
        using (new FileStream(Path.Combine(foreign, KotlinSystemPath.ServerLockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var lease = KotlinSystemPath.Acquire(server, "--stdio --system-path=\"" + foreign + "\"", Path.Combine(root, "default")))
            check(lease.Slot == 2 && lease.SystemPath == foreign + "-2", "A path whose server lock is held by another server is skipped");
        File.WriteAllText(Path.Combine(foreign, KotlinSystemPath.ServerLockFileName), "");
        using (var lease = KotlinSystemPath.Acquire(server, "--stdio --system-path=\"" + foreign + "\"", Path.Combine(root, "default")))
            check(lease.Slot == 1, "A leftover server lock file that nobody holds does not block the path");

        using (var other = KotlinSystemPath.Acquire(Path.Combine(root, "bin", "kotlin-language-server.bat"), "--verbose", Path.Combine(root, "default")))
            check(other.Slot == 0 && other.SystemPath == null && other.Arguments == "--verbose", "Other servers keep their arguments");
        using (var official = KotlinSystemPath.Acquire(Path.Combine(root, "bin", "kotlin-lsp.cmd"), "--stdio", Path.Combine(root, "default")))
            check(official.Slot == 1 && official.SystemPath == Path.Combine(root, "default") && official.Arguments.EndsWith("--system-path=\"" + Path.Combine(root, "default") + "\""),
                "The official launcher without a system path gets Vistudio's default path");
    }
}
