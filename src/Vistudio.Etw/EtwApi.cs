using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vistudio.Etw;

/// <summary>EVENT_DESCRIPTOR.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct EtwEventDescriptor
{
    public readonly ushort Id;
    public readonly byte Version;
    public readonly byte Channel;
    public readonly byte Level;
    public readonly byte Opcode;
    public readonly ushort Task;
    public readonly ulong Keyword;

    public EtwEventDescriptor(ushort id, byte version, byte channel, byte level, byte opcode, ushort task, ulong keyword)
    {
        Id = id; Version = version; Channel = channel; Level = level; Opcode = opcode; Task = task; Keyword = keyword;
    }
}

/// <summary>EVENT_DATA_DESCRIPTOR_TYPE_*.</summary>
public enum EtwSegmentType : byte
{
    Data = 0,
    EventMetadata = 1,
    ProviderMetadata = 2,
}

/// <summary>One EVENT_DATA_DESCRIPTOR passed to EventWrite.</summary>
public readonly struct EtwSegment
{
    public EtwSegment(EtwSegmentType type, byte[] bytes) { Type = type; Bytes = bytes; }
    public EtwSegmentType Type { get; }
    public byte[] Bytes { get; }
}

/// <summary>The advapi32 provider functions. Swappable so registration and writes can be unit-tested.</summary>
public interface IEtwApi
{
    uint Register(Guid providerId, out ulong handle);
    uint SetProviderTraits(ulong handle, byte[] traits);
    bool IsEnabled(ulong handle, byte level, ulong keyword);
    uint Write(ulong handle, in EtwEventDescriptor descriptor, IReadOnlyList<EtwSegment> segments);
    uint Unregister(ulong handle);
}

/// <summary>EventRegister / EventSetInformation / EventProviderEnabled / EventWrite / EventUnregister.</summary>
public sealed class AdvapiEtwApi : IEtwApi
{
    public static readonly AdvapiEtwApi Instance = new AdvapiEtwApi();
    private const int EventProviderSetTraits = 2;

    private AdvapiEtwApi() { }

    public uint Register(Guid providerId, out ulong handle) => EventRegister(ref providerId, IntPtr.Zero, IntPtr.Zero, out handle);

    public uint SetProviderTraits(ulong handle, byte[] traits) => EventSetInformation(handle, EventProviderSetTraits, traits, (uint)traits.Length);

    public bool IsEnabled(ulong handle, byte level, ulong keyword) => EventProviderEnabled(handle, level, keyword);

    public unsafe uint Write(ulong handle, in EtwEventDescriptor descriptor, IReadOnlyList<EtwSegment> segments)
    {
        var count = segments.Count;
        var data = stackalloc EtwDataDescriptor[count];
        var pins = new GCHandle[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                pins[i] = GCHandle.Alloc(segments[i].Bytes, GCHandleType.Pinned);
                data[i] = new EtwDataDescriptor
                {
                    Ptr = (ulong)pins[i].AddrOfPinnedObject().ToInt64(),
                    Size = (uint)segments[i].Bytes.Length,
                    Type = (byte)segments[i].Type,
                };
            }
            var copy = descriptor;
            return EventWrite(handle, ref copy, (uint)count, data);
        }
        finally
        {
            foreach (var pin in pins) if (pin.IsAllocated) pin.Free();
        }
    }

    public uint Unregister(ulong handle) => EventUnregister(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct EtwDataDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public byte Type;
        public byte Reserved1;
        public ushort Reserved2;
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EventRegister(ref Guid providerId, IntPtr enableCallback, IntPtr callbackContext, out ulong handle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EventSetInformation(ulong handle, int informationClass, byte[] information, uint length);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool EventProviderEnabled(ulong handle, byte level, ulong keyword);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern unsafe uint EventWrite(ulong handle, ref EtwEventDescriptor descriptor, uint count, EtwDataDescriptor* data);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EventUnregister(ulong handle);
}
