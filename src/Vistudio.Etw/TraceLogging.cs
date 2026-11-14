using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vistudio.Etw;

/// <summary>TraceLogging field input types (TlgIn*). Only the ones the runtime events use.</summary>
public enum TraceLoggingType : byte
{
    UnicodeString = 1,
    Int32 = 7,
    UInt32 = 8,
    UInt64 = 10,
    HexInt64 = 21,
}

public readonly struct TraceLoggingField
{
    public TraceLoggingField(string name, TraceLoggingType type, bool isArray = false) { Name = name; Type = type; IsArray = isArray; }
    public string Name { get; }
    public TraceLoggingType Type { get; }
    /// <summary>Variable-length array: a UInt16 count precedes the elements in the payload.</summary>
    public bool IsArray { get; }
}

/// <summary>
/// Self-describing (TraceLogging) metadata. Events carry their own names and field layout,
/// so the profiler decodes them without an installed manifest.
/// </summary>
public static class TraceLogging
{
    /// <summary>WINEVENT_CHANNEL_TRACELOGGING: tells decoders the event carries TraceLogging metadata.</summary>
    public const byte Channel = 11;
    internal const byte VariableCountFlag = 0x40;

    /// <summary>Provider traits: UInt16 total size, UTF-8 name, NUL.</summary>
    public static byte[] ProviderTraits(string providerName) => SizePrefixed(w => WriteName(w, providerName));

    /// <summary>Event metadata: UInt16 total size, tags byte (0), UTF-8 event name, NUL, then per field: UTF-8 name, NUL, input type.</summary>
    public static byte[] EventMetadata(string eventName, IReadOnlyList<TraceLoggingField> fields)
    {
        return SizePrefixed(w =>
        {
            w.Write((byte)0);
            WriteName(w, eventName);
            foreach (var field in fields)
            {
                WriteName(w, field.Name);
                w.Write((byte)((byte)field.Type | (field.IsArray ? VariableCountFlag : 0)));
            }
        });
    }

    private static byte[] SizePrefixed(Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            body(writer);
        }
        var bytes = stream.ToArray();
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("TraceLogging metadata exceeds 64 KB.");
        bytes[0] = (byte)bytes.Length;
        bytes[1] = (byte)(bytes.Length >> 8);
        return bytes;
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0) throw new ArgumentException("Names must be non-empty and contain no NUL.");
        writer.Write(Encoding.UTF8.GetBytes(name));
        writer.Write((byte)0);
    }
}

/// <summary>Little-endian payload in the field order of the event metadata.</summary>
internal sealed class TraceLoggingPayload
{
    private readonly MemoryStream stream = new MemoryStream(64);
    private readonly BinaryWriter writer;

    public TraceLoggingPayload() { writer = new BinaryWriter(stream, Encoding.Unicode); }

    public TraceLoggingPayload UInt32(uint value) { writer.Write(value); return this; }
    public TraceLoggingPayload Int32(int value) { writer.Write(value); return this; }
    public TraceLoggingPayload UInt64(ulong value) { writer.Write(value); return this; }

    /// <summary>NUL-terminated UTF-16; embedded NULs would cut the field, so they are dropped.</summary>
    public TraceLoggingPayload String(string? value)
    {
        foreach (var c in value ?? string.Empty) if (c != '\0') writer.Write((ushort)c);
        writer.Write((ushort)0);
        return this;
    }

    public TraceLoggingPayload UInt64Array(IReadOnlyList<ulong> values, int count)
    {
        writer.Write((ushort)count);
        for (var i = 0; i < count; i++) writer.Write(values[i]);
        return this;
    }

    public byte[] ToArray() { writer.Flush(); return stream.ToArray(); }
}
