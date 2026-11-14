using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vistudio.Core.Zig;

namespace Vistudio.Core.Dart
{
    /// <summary>
    /// Command-line handling for dart.exe and flutter.bat. dart.exe is started directly; flutter.bat needs cmd.exe, so
    /// its arguments are each double-quoted and characters that cmd.exe still expands inside quotes are rejected.
    /// </summary>
    public static class DartArguments
    {
        /// <summary>Splits user arguments on whitespace; double quotes group an argument.</summary>
        public static IReadOnlyList<string> Split(string? text) => ZigArguments.Split(text, "Dart arguments");

        /// <summary>Quotes one argument for CommandLineToArgvW (dart.exe).</summary>
        public static string Quote(string argument) => ZigArguments.Quote(argument);

        public static string Join(IEnumerable<string> arguments) => ZigArguments.Join(arguments);

        /// <summary>A Flutter device id from <c>flutter devices</c>: letters, digits and <c>-_.:</c> only.</summary>
        public static string ValidateDevice(string device)
        {
            var value = device.Trim();
            if (value.Length == 0 || value.StartsWith("-", StringComparison.Ordinal) || value.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ':')))
                throw new ArgumentException("Invalid Flutter device id: '" + device + "'.");
            return value;
        }

        /// <summary>
        /// The command line for <c>cmd.exe /d /s /c</c> running a batch file. Every argument is double-quoted, which
        /// keeps <c>&amp; | &lt; &gt; ^ ( )</c> literal; <c>%</c> (expanded even inside quotes), <c>"</c> and line breaks
        /// cannot be passed safely and are rejected.
        /// </summary>
        public static string BatchCommandLine(string batch, IEnumerable<string> arguments)
        {
            var builder = new StringBuilder("/d /v:off /s /c \"");
            builder.Append('"').Append(CheckBatch(batch, "Batch file path")).Append('"');
            foreach (var argument in arguments)
                builder.Append(" \"").Append(CheckBatch(argument, "Argument of " + Path.GetFileName(batch))).Append('"');
            return builder.Append('"').ToString();
        }

        private static string CheckBatch(string value, string what)
        {
            if (value.IndexOfAny(new[] { '%', '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException(what + " cannot contain %, double quotes or line breaks (it is passed through cmd.exe): " + value);
            if (value.EndsWith("\\", StringComparison.Ordinal)) value += "."; // a trailing backslash would escape the closing quote
            return value;
        }
    }
}
