using System.Diagnostics.Tracing;

namespace Vistudio.Etw;

/// <summary>
/// Runtime profiling events. Implemented by <see cref="RuntimeEtwProvider"/> (EventRegister/EventWrite)
/// and by <see cref="RuntimeEventSource"/> (EventSource, for a .NET-hosted runtime). Both use the same
/// event names, ids, levels, keywords and field names. Writes never throw.
/// </summary>
public interface IRuntimeEvents
{
    bool IsEnabled(EventLevel level, EventKeywords keywords);
    void SessionStart(ulong SessionId, string RuntimeName, string RuntimeVersion);
    void SessionStop(ulong SessionId, int ExitCode);
    /// <summary>Frames are instruction pointers or runtime method ids, innermost first; at most <see cref="RuntimeEventIds.MaxStackFrames"/> are written.</summary>
    void StackSample(ulong SessionId, uint ThreadId, ulong[] Frames);
    void Allocation(ulong SessionId, ulong Address, ulong Size, string TypeName);
}

/// <summary>Shared event contract.</summary>
public static class RuntimeEventIds
{
    public const int SessionStart = 1;
    public const int SessionStop = 2;
    public const int StackSample = 3;
    public const int Allocation = 4;

    public const EventKeywords SessionKeyword = (EventKeywords)0x1;
    public const EventKeywords SamplingKeyword = (EventKeywords)0x2;
    public const EventKeywords AllocationKeyword = (EventKeywords)0x4;

    public const EventTask SessionTask = (EventTask)1;
    public const EventTask StackSampleTask = (EventTask)2;
    public const EventTask AllocationTask = (EventTask)3;

    /// <summary>Keeps a sample well under the 64 KB ETW event limit.</summary>
    public const int MaxStackFrames = 4096;
}
