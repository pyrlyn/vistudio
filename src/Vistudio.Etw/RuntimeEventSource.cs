using System;
using System.Diagnostics.Tracing;

namespace Vistudio.Etw;

/// <summary>
/// EventSource "Vistudio-Runtime-Managed" for a runtime hosted on .NET. Same events and fields as
/// <see cref="RuntimeEtwProvider"/>; self-describing format so the frame array needs no manifest
/// support. Also visible to in-process <see cref="EventListener"/>s and EventPipe.
/// </summary>
[EventSource(Name = SourceName)]
public sealed class RuntimeEventSource : EventSource, IRuntimeEvents
{
    public const string SourceName = "Vistudio-Runtime-Managed";
    /// <summary>Equals <see cref="EventSource.GetGuid"/> for this type and <see cref="EtwProviderId.FromName"/> of <see cref="SourceName"/>.</summary>
    public static readonly Guid SourceId = new Guid("e74c385b-8523-59ba-af56-0e6de7ae32b6");

    public static readonly RuntimeEventSource Log = new RuntimeEventSource();

    private RuntimeEventSource() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }

    public static class Keywords
    {
        public const EventKeywords Session = RuntimeEventIds.SessionKeyword;
        public const EventKeywords Sampling = RuntimeEventIds.SamplingKeyword;
        public const EventKeywords Allocation = RuntimeEventIds.AllocationKeyword;
    }

    public static class Tasks
    {
        public const EventTask Session = RuntimeEventIds.SessionTask;
        public const EventTask StackSample = RuntimeEventIds.StackSampleTask;
        public const EventTask Allocation = RuntimeEventIds.AllocationTask;
    }

    [Event(RuntimeEventIds.SessionStart, Level = EventLevel.Informational, Keywords = Keywords.Session, Task = Tasks.Session, Opcode = EventOpcode.Start)]
    public void SessionStart(ulong SessionId, string RuntimeName, string RuntimeVersion)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Session))
            WriteEvent(RuntimeEventIds.SessionStart, SessionId, RuntimeName ?? string.Empty, RuntimeVersion ?? string.Empty);
    }

    [Event(RuntimeEventIds.SessionStop, Level = EventLevel.Informational, Keywords = Keywords.Session, Task = Tasks.Session, Opcode = EventOpcode.Stop)]
    public void SessionStop(ulong SessionId, int ExitCode)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Session))
            WriteEvent(RuntimeEventIds.SessionStop, SessionId, ExitCode);
    }

    [Event(RuntimeEventIds.StackSample, Level = EventLevel.Verbose, Keywords = Keywords.Sampling, Task = Tasks.StackSample, Opcode = EventOpcode.Info)]
    public void StackSample(ulong SessionId, uint ThreadId, ulong[] Frames)
    {
        if (!IsEnabled(EventLevel.Verbose, Keywords.Sampling)) return;
        var frames = Frames ?? Array.Empty<ulong>();
        if (frames.Length > RuntimeEventIds.MaxStackFrames)
        {
            var clipped = new ulong[RuntimeEventIds.MaxStackFrames];
            Array.Copy(frames, clipped, clipped.Length);
            frames = clipped;
        }
        WriteEvent(RuntimeEventIds.StackSample, SessionId, ThreadId, frames);
    }

    [Event(RuntimeEventIds.Allocation, Level = EventLevel.Verbose, Keywords = Keywords.Allocation, Task = Tasks.Allocation, Opcode = EventOpcode.Info)]
    public void Allocation(ulong SessionId, ulong Address, ulong Size, string TypeName)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Allocation))
            WriteEvent(RuntimeEventIds.Allocation, SessionId, Address, Size, TypeName ?? string.Empty);
    }
}
