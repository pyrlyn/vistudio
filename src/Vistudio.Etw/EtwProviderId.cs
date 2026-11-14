using System;
using System.Security.Cryptography;

namespace Vistudio.Etw;

/// <summary>
/// Provider GUID derived from the provider name with the algorithm EventSource and TraceLogging use,
/// so tools that accept a provider name (PerfView "*Name", tracelog, WPR) resolve to the same GUID.
/// </summary>
public static class EtwProviderId
{
    private static readonly byte[] Namespace =
        { 0x48, 0x2C, 0x2D, 0xB2, 0xC3, 0x90, 0x47, 0xC8, 0x87, 0xF8, 0x1A, 0x15, 0xBF, 0xC1, 0x30, 0xFB };

    public static Guid FromName(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Provider name is required.", nameof(name));
        var upper = name.ToUpperInvariant();
        var input = new byte[Namespace.Length + upper.Length * 2];
        Namespace.CopyTo(input, 0);
        for (var i = 0; i < upper.Length; i++)
        {
            input[Namespace.Length + i * 2] = (byte)(upper[i] >> 8);
            input[Namespace.Length + i * 2 + 1] = (byte)upper[i];
        }
        byte[] hash;
        using (var sha1 = SHA1.Create()) hash = sha1.ComputeHash(input);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        return new Guid(bytes);
    }
}
