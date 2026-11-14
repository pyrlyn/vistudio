using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Vistudio.Etw;

/// <summary>
/// ETW provider "Vistudio-Runtime" registered with EventRegister and written with EventWrite.
/// Events are self-describing (TraceLogging), so the Visual Studio Events Viewer shows names and
/// fields after the GUID is added under Additional Providers; no manifest has to be installed.
/// </summary>
public sealed class RuntimeEtwProvider : IRuntimeEvents, IDisposable
{
    public const string ProviderName = "Vistudio-Runtime";
    /// <summary>Equals <see cref="EtwProviderId.FromName"/> of <see cref="ProviderName"/>.</summary>
    public static readonly Guid ProviderId = new Guid("4f3c063f-5474-59d2-14b2-3d8687b788b9");

    internal static readonly RuntimeEventDefinition SessionStartEvent = new RuntimeEventDefinition(
        "SessionStart", RuntimeEventIds.SessionStart, EventLevel.Informational, EventOpcode.Start, RuntimeEventIds.SessionTask, RuntimeEventIds.SessionKeyword,
        new TraceLoggingField("SessionId", TraceLoggingType.UInt64),
        new TraceLoggingField("RuntimeName", TraceLoggingType.UnicodeString),
        new TraceLoggingField("RuntimeVersion", TraceLoggingType.UnicodeString));

    internal static readonly RuntimeEventDefinition SessionStopEvent = new RuntimeEventDefinition(
        "SessionStop", RuntimeEventIds.SessionStop, EventLevel.Informational, EventOpcode.Stop, RuntimeEventIds.SessionTask, RuntimeEventIds.SessionKeyword,
        new TraceLoggingField("SessionId", TraceLoggingType.UInt64),
        new TraceLoggingField("ExitCode", TraceLoggingType.Int32));

    internal static readonly RuntimeEventDefinition StackSampleEvent = new RuntimeEventDefinition(
        "StackSample", RuntimeEventIds.StackSample, EventLevel.Verbose, EventOpcode.Info, RuntimeEventIds.StackSampleTask, RuntimeEventIds.SamplingKeyword,
        new TraceLoggingField("SessionId", TraceLoggingType.UInt64),
        new TraceLoggingField("ThreadId", TraceLoggingType.UInt32),
        new TraceLoggingField("Frames", TraceLoggingType.HexInt64, isArray: true));

    internal static readonly RuntimeEventDefinition AllocationEvent = new RuntimeEventDefinition(
        "Allocation", RuntimeEventIds.Allocation, EventLevel.Verbose, EventOpcode.Info, RuntimeEventIds.AllocationTask, RuntimeEventIds.AllocationKeyword,
        new TraceLoggingField("SessionId", TraceLoggingType.UInt64),
        new TraceLoggingField("Address", TraceLoggingType.HexInt64),
        new TraceLoggingField("Size", TraceLoggingType.UInt64),
        new TraceLoggingField("TypeName", TraceLoggingType.UnicodeString));

    private readonly IEtwApi api;
    private readonly byte[] traits;
    private long handle; // ulong REGHANDLE; long because .NET Framework Interlocked has no ulong overloads.
    private uint lastWriteStatus;

    /// <summary>Registers with the real advapi32 functions.</summary>
    public RuntimeEtwProvider() : this(AdvapiEtwApi.Instance) { }

    /// <summary>Registers through <paramref name="api"/>. A failed registration leaves an inert provider; see <see cref="RegistrationStatus"/>.</summary>
    public RuntimeEtwProvider(IEtwApi api, string providerName = ProviderName)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        Name = providerName;
        Id = providerName == ProviderName ? ProviderId : EtwProviderId.FromName(providerName);
        traits = TraceLogging.ProviderTraits(providerName);
        RegistrationStatus = api.Register(Id, out var registered);
        if (RegistrationStatus != 0) return;
        handle = (long)registered;
        // Win32 error is ignored on systems without provider traits; events still carry the provider metadata segment.
        TraitsStatus = api.SetProviderTraits(registered, traits);
    }

    public string Name { get; }
    public Guid Id { get; }
    /// <summary>Win32 status of EventRegister (0 = success).</summary>
    public uint RegistrationStatus { get; }
    /// <summary>Win32 status of EventSetInformation(EventProviderSetTraits).</summary>
    public uint TraitsStatus { get; }
    public bool IsRegistered => (ulong)Interlocked.Read(ref handle) != 0;
    /// <summary>Win32 status of the last EventWrite (0 = success, 234 = event too large, 8 = no buffers).</summary>
    public uint LastWriteStatus => Volatile.Read(ref lastWriteStatus);

    /// <summary>True when a session (for example the Visual Studio profiler) enabled this level and any of these keywords.</summary>
    public bool IsEnabled(EventLevel level, EventKeywords keywords)
    {
        var current = (ulong)Interlocked.Read(ref handle);
        return current != 0 && api.IsEnabled(current, (byte)level, (ulong)keywords);
    }

    public void SessionStart(ulong SessionId, string RuntimeName, string RuntimeVersion)
    {
        if (!IsEnabled(SessionStartEvent)) return;
        Write(SessionStartEvent, new TraceLoggingPayload().UInt64(SessionId).String(RuntimeName).String(RuntimeVersion));
    }

    public void SessionStop(ulong SessionId, int ExitCode)
    {
        if (!IsEnabled(SessionStopEvent)) return;
        Write(SessionStopEvent, new TraceLoggingPayload().UInt64(SessionId).Int32(ExitCode));
    }

    public void StackSample(ulong SessionId, uint ThreadId, ulong[] Frames)
    {
        if (!IsEnabled(StackSampleEvent)) return;
        var frames = Frames ?? Array.Empty<ulong>();
        var count = Math.Min(frames.Length, RuntimeEventIds.MaxStackFrames);
        Write(StackSampleEvent, new TraceLoggingPayload().UInt64(SessionId).UInt32(ThreadId).UInt64Array(frames, count));
    }

    public void Allocation(ulong SessionId, ulong Address, ulong Size, string TypeName)
    {
        if (!IsEnabled(AllocationEvent)) return;
        Write(AllocationEvent, new TraceLoggingPayload().UInt64(SessionId).UInt64(Address).UInt64(Size).String(TypeName));
    }

    public void Dispose()
    {
        var current = (ulong)Interlocked.Exchange(ref handle, 0);
        if (current != 0) api.Unregister(current);
    }

    private bool IsEnabled(RuntimeEventDefinition definition)
    {
        var current = (ulong)Interlocked.Read(ref handle);
        return current != 0 && api.IsEnabled(current, definition.Descriptor.Level, definition.Descriptor.Keyword);
    }

    private void Write(RuntimeEventDefinition definition, TraceLoggingPayload payload)
    {
        var current = (ulong)Interlocked.Read(ref handle);
        if (current == 0) return;
        var segments = new[]
        {
            new EtwSegment(EtwSegmentType.ProviderMetadata, traits),
            new EtwSegment(EtwSegmentType.EventMetadata, definition.Metadata),
            new EtwSegment(EtwSegmentType.Data, payload.ToArray()),
        };
        try { Volatile.Write(ref lastWriteStatus, api.Write(current, definition.Descriptor, segments)); }
        catch (Exception) { Volatile.Write(ref lastWriteStatus, uint.MaxValue); }
    }
}

internal sealed class RuntimeEventDefinition
{
    public RuntimeEventDefinition(string name, int id, EventLevel level, EventOpcode opcode, EventTask task, EventKeywords keywords, params TraceLoggingField[] fields)
    {
        Name = name;
        Fields = fields;
        Descriptor = new EtwEventDescriptor((ushort)id, 0, TraceLogging.Channel, (byte)level, (byte)opcode, (ushort)task, (ulong)keywords);
        Metadata = TraceLogging.EventMetadata(name, fields);
    }

    public string Name { get; }
    public TraceLoggingField[] Fields { get; }
    public EtwEventDescriptor Descriptor { get; }
    public byte[] Metadata { get; }
}
