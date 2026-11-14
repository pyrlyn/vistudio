using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vistudio.Core.Packages
{
    /// <summary>
    /// Guards for text that reaches a package manager's command line or manifest. Names, versions and sources come
    /// from a dialog or from another manifest, so they are untrusted: anything that could end an argument, start an
    /// option or be expanded by cmd.exe (batch launchers such as gradlew.bat and flutter.bat) is rejected.
    /// </summary>
    public static class PackageArguments
    {
        public static string ValidateName(string name) => Validate(name, "Package name", "@._+-/:");

        /// <summary>Versions and constraints: <c>1.2.3</c>, <c>^1.2.0</c>, <c>~1.4</c>, <c>&gt;=1.0 &lt;2.0</c>, <c>[1.0,2.0)</c>, <c>1.*</c>.</summary>
        public static string ValidateVersion(string version) => Validate(version, "Version", "._+-^~<>=*,[]() ");

        /// <summary>URLs and paths (Zig dependencies, registries).</summary>
        public static string ValidateSource(string source) => Validate(source, "Source", "@._+-/:\\#?=~ ");

        private static string Validate(string value, string what, string allowed)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var text = value.Trim();
            if (text.Length == 0) throw new ArgumentException(what + " is empty.");
            if (text[0] == '-') throw new ArgumentException(what + " cannot start with '-': " + text);
            foreach (var c in text)
                if (!(char.IsLetterOrDigit(c) && c < 128) && allowed.IndexOf(c) < 0)
                    throw new ArgumentException(what + " contains an unsupported character '" + c + "': " + text);
            return text;
        }

        /// <summary>Joins arguments for CreateProcess with the quoting rules of CommandLineToArgvW (shared with Zig and Dart).</summary>
        public static string Join(IEnumerable<string> arguments) => Zig.ZigArguments.Join(arguments);

        public static string Quote(string argument) => Zig.ZigArguments.Quote(argument ?? throw new ArgumentNullException(nameof(argument)));
    }

    /// <summary>
    /// Version ordering shared by every manager: dot-separated segments compared numerically where both are numbers,
    /// a pre-release (<c>-rc.1</c>) sorts before its release, build metadata (<c>+5</c>) is ignored, and a leading
    /// <c>v</c> or constraint operator (<c>^</c>, <c>~</c>, <c>=</c>, <c>&gt;=</c>) is skipped.
    /// </summary>
    public static class PackageVersion
    {
        public static int Compare(string left, string right)
        {
            Split(left, out var leftCore, out var leftPre);
            Split(right, out var rightCore, out var rightPre);
            var core = CompareSegments(leftCore, rightCore, true);
            if (core != 0) return core;
            if (leftPre.Length == 0 || rightPre.Length == 0) return leftPre.Length == 0 ? (rightPre.Length == 0 ? 0 : 1) : -1;
            return CompareSegments(leftPre, rightPre, false);
        }

        public static bool Same(string? left, string? right) => left != null && right != null && Compare(left, right) == 0;

        /// <summary>The newest of <paramref name="versions"/>; pre-releases only when nothing else exists or <paramref name="includePreRelease"/>.</summary>
        public static string? Newest(IEnumerable<string> versions, bool includePreRelease = false)
        {
            var list = versions.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var stable = list.Where(v => !IsPreRelease(v)).ToList();
            var pool = includePreRelease || stable.Count == 0 ? list : stable;
            string? best = null;
            foreach (var version in pool) if (best == null || Compare(version, best) > 0) best = version;
            return best;
        }

        public static bool IsPreRelease(string version)
        {
            Split(version, out _, out var pre);
            return pre.Length != 0;
        }

        private static void Split(string version, out string[] core, out string[] pre)
        {
            var text = (version ?? "").Trim().TrimStart('^', '~', '=', '>', '<', ' ');
            if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1])) text = text.Substring(1);
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);
            var dash = text.IndexOf('-');
            core = (dash < 0 ? text : text.Substring(0, dash)).Split('.');
            pre = dash < 0 ? new string[0] : text.Substring(dash + 1).Split('.');
        }

        private static int CompareSegments(string[] left, string[] right, bool padWithZero)
        {
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                if (i >= left.Length || i >= right.Length)
                {
                    // 1.2 equals 1.2.0, but a longer pre-release (rc.1) is newer than its prefix (rc).
                    var rest = i >= left.Length ? right : left;
                    if (padWithZero && rest.Skip(i).All(s => long.TryParse(s, out var n) && n == 0)) return 0;
                    return i >= left.Length ? -1 : 1;
                }
                var leftNumber = long.TryParse(left[i], out var l);
                var rightNumber = long.TryParse(right[i], out var r);
                int result;
                if (leftNumber && rightNumber) result = l.CompareTo(r);
                else if (leftNumber != rightNumber) result = leftNumber ? -1 : 1;
                else result = string.CompareOrdinal(left[i], right[i]);
                if (result != 0) return result < 0 ? -1 : 1;
            }
            return 0;
        }
    }
}
