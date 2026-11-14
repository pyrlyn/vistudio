using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using System.Threading;
using Vistudio.Etw;

internal static class EtwTests
{
    internal static void Run(Action<bool, string> check)
    {
        ProviderIds(check);
        Registration(check);
        Writes(check);
        EnableFiltering(check);
        WriteFailures(check);
        RealAdvapi(check);
        EventSourceEvents(check);
    }

    /// <summary>Emits every event from both providers for a real ETW session (tests/verify-etw.ps1, or the Visual Studio profiler).</summary>
    internal static int Emit(int seconds)
    {
        using var provider = new RuntimeEtwProvider();
        if (!provider.IsRegistered) { Console.WriteLine("FAIL: EventRegister returned " + provider.RegistrationStatus); return 1; }
        IRuntimeEvents[] sinks = { provider, RuntimeEventSource.Log };
#pragma warning disable CS0618 // The OS thread id is what the profiler shows.
        var threadId = (uint)AppDomain.GetCurrentThreadId();
#pragma warning restore CS0618
        var frames = new ulong[] { 0x7FF6_0000_1000, 0x7FF6_0000_2000, 0x7FF6_0000_3000 };
        foreach (var sink in sinks) sink.SessionStart(1, "Vistudio sample runtime", "1.0");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var samples = 0;
        do
        {
            foreach (var sink in sinks)
            {
                sink.StackSample(1, threadId, frames);
                sink.Allocation(1, 0x1_0000_0000UL + (ulong)samples * 64, 64, "sample.Node");
            }
            samples++;
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
        foreach (var sink in sinks) sink.SessionStop(1, 0);
        Console.WriteLine("Emitted " + samples + " samples; provider " + provider.Id + " last EventWrite status " + provider.LastWriteStatus
            + "; EventSource " + RuntimeEventSource.SourceId + ".");
        return provider.LastWriteStatus == 0 ? 0 : 1;
    }

    private static void ProviderIds(Action<bool, string> check)
    {
        check(EtwProviderId.FromName(RuntimeEtwProvider.ProviderName) == RuntimeEtwProvider.ProviderId, "Native provider GUID is the name hash");
        check(EtwProviderId.FromName("vistudio-runtime") == RuntimeEtwProvider.ProviderId, "Name hash ignores case like EventSource");
        using (var probe = new EventSource(RuntimeEtwProvider.ProviderName))
            check(probe.Guid == RuntimeEtwProvider.ProviderId, "EventSource derives the same GUID from the native provider name");
        check(EventSource.GetGuid(typeof(RuntimeEventSource)) == RuntimeEventSource.SourceId, "EventSource GUID constant matches the type");
        check(EtwProviderId.FromName(RuntimeEventSource.SourceName) == RuntimeEventSource.SourceId, "EventSource GUID is the name hash");
        check(RuntimeEventSource.SourceId != RuntimeEtwProvider.ProviderId, "Managed and native providers have distinct GUIDs");
        var threw = false;
        try { EtwProviderId.FromName(""); } catch (ArgumentException) { threw = true; }
        check(threw, "Empty provider name is rejected");
    }

    private static void Registration(Action<bool, string> check)
    {
        var api = new RecordingEtwApi();
        var provider = new RuntimeEtwProvider(api);
        check(api.RegisterCalls == 1 && api.RegisteredId == RuntimeEtwProvider.ProviderId, "EventRegister is called once with the provider GUID");
        check(provider.IsRegistered && provider.RegistrationStatus == 0 && provider.TraitsStatus == 0, "Provider reports a successful registration");
        var traits = api.Traits!;
        check(traits.Length == 2 + RuntimeEtwProvider.ProviderName.Length + 1 && BitConverter.ToUInt16(traits, 0) == traits.Length,
            "Provider traits start with their total size");
        check(Encoding.UTF8.GetString(traits, 2, traits.Length - 3) == RuntimeEtwProvider.ProviderName && traits[traits.Length - 1] == 0,
            "Provider traits carry the NUL-terminated UTF-8 provider name");
        check(provider.Name == RuntimeEtwProvider.ProviderName && provider.Id == RuntimeEtwProvider.ProviderId, "Name and GUID are exposed");

        provider.Dispose();
        check(api.UnregisterCalls == 1 && api.UnregisteredHandle == RecordingEtwApi.Handle, "Dispose calls EventUnregister with the handle");
        provider.Dispose();
        check(api.UnregisterCalls == 1, "Second Dispose does not unregister again");
        provider.SessionStart(1, "r", "v");
        check(api.Writes.Count == 0 && !provider.IsRegistered && !provider.IsEnabled(EventLevel.LogAlways, RuntimeEventIds.SessionKeyword),
            "A disposed provider writes nothing and is disabled");

        var failing = new RecordingEtwApi { RegisterResult = 5 };
        using (var inert = new RuntimeEtwProvider(failing))
        {
            inert.SessionStart(1, "r", "v");
            inert.Allocation(1, 2, 3, "t");
            check(!inert.IsRegistered && inert.RegistrationStatus == 5 && failing.Traits == null && failing.Writes.Count == 0,
                "Failed EventRegister leaves an inert provider without traits or writes");
        }
        check(failing.UnregisterCalls == 0, "An unregistered provider is not unregistered on Dispose");

        var custom = new RecordingEtwApi();
        using (var named = new RuntimeEtwProvider(custom, "Contoso-Lang"))
            check(named.Id == EtwProviderId.FromName("Contoso-Lang") && custom.RegisteredId == named.Id, "A custom provider name registers its own name hash GUID");
    }

    private static void Writes(Action<bool, string> check)
    {
        var api = new RecordingEtwApi();
        using var provider = new RuntimeEtwProvider(api);

        provider.SessionStart(42, "Kot\0lin", "2.4.21");
        var start = api.Writes.Single();
        CheckEnvelope(check, api, start, "SessionStart", RuntimeEventIds.SessionStart, EventLevel.Informational, EventOpcode.Start,
            RuntimeEventIds.SessionTask, RuntimeEventIds.SessionKeyword, ("SessionId", 10), ("RuntimeName", 1), ("RuntimeVersion", 1));
        var values = Decode(start);
        check((ulong)values[0] == 42 && (string)values[1] == "Kotlin" && (string)values[2] == "2.4.21", "SessionStart payload, embedded NUL dropped");

        provider.SessionStart(43, null!, null!);
        values = Decode(api.Writes.Last());
        check((string)values[1] == "" && (string)values[2] == "", "Null strings are written as empty strings");

        api.Writes.Clear();
        provider.SessionStop(42, -3);
        CheckEnvelope(check, api, api.Writes.Single(), "SessionStop", RuntimeEventIds.SessionStop, EventLevel.Informational, EventOpcode.Stop,
            RuntimeEventIds.SessionTask, RuntimeEventIds.SessionKeyword, ("SessionId", 10), ("ExitCode", 7));
        values = Decode(api.Writes.Single());
        check((ulong)values[0] == 42 && (int)values[1] == -3, "SessionStop payload");

        api.Writes.Clear();
        provider.StackSample(42, 7, new ulong[] { 0x7FF6_0000_1000, 0x2000, ulong.MaxValue });
        CheckEnvelope(check, api, api.Writes.Single(), "StackSample", RuntimeEventIds.StackSample, EventLevel.Verbose, EventOpcode.Info,
            RuntimeEventIds.StackSampleTask, RuntimeEventIds.SamplingKeyword, ("SessionId", 10), ("ThreadId", 8), ("Frames", 21 | 0x40));
        values = Decode(api.Writes.Single());
        check((ulong)values[0] == 42 && (uint)values[1] == 7 && ((ulong[])values[2]).SequenceEqual(new ulong[] { 0x7FF6_0000_1000, 0x2000, ulong.MaxValue }),
            "StackSample payload carries a counted hex frame array");

        api.Writes.Clear();
        provider.StackSample(1, 1, Enumerable.Range(0, RuntimeEventIds.MaxStackFrames + 10).Select(i => (ulong)i).ToArray());
        provider.StackSample(1, 1, null!);
        var clipped = (ulong[])Decode(api.Writes[0])[2];
        check(clipped.Length == RuntimeEventIds.MaxStackFrames && clipped[clipped.Length - 1] == RuntimeEventIds.MaxStackFrames - 1,
            "Deep stacks are clipped to the innermost MaxStackFrames frames");
        check(((ulong[])Decode(api.Writes[1])[2]).Length == 0, "A null stack is an empty frame array");
        check(api.Writes[0].Segments[2].Bytes.Length < 64 * 1024, "A clipped sample stays under the 64 KB ETW limit");

        api.Writes.Clear();
        provider.Allocation(42, 0x1_0000_0040, 128, "app.Node<T>");
        CheckEnvelope(check, api, api.Writes.Single(), "Allocation", RuntimeEventIds.Allocation, EventLevel.Verbose, EventOpcode.Info,
            RuntimeEventIds.AllocationTask, RuntimeEventIds.AllocationKeyword, ("SessionId", 10), ("Address", 21), ("Size", 10), ("TypeName", 1));
        values = Decode(api.Writes.Single());
        check((ulong)values[0] == 42 && (ulong)values[1] == 0x1_0000_0040 && (ulong)values[2] == 128 && (string)values[3] == "app.Node<T>",
            "Allocation payload");
        check(provider.LastWriteStatus == 0, "Successful EventWrite status is kept");
    }

    private static void EnableFiltering(Action<bool, string> check)
    {
        // A session at Informational level with only the Session keyword, as ETW evaluates it: level <= session level, keyword overlaps the mask.
        var api = new RecordingEtwApi { SessionLevel = (byte)EventLevel.Informational, SessionKeywords = (ulong)RuntimeEventIds.SessionKeyword };
        using var provider = new RuntimeEtwProvider(api);
        provider.SessionStart(1, "r", "v");
        provider.StackSample(1, 1, new ulong[] { 1 });
        provider.Allocation(1, 2, 3, "t");
        provider.SessionStop(1, 0);
        check(api.Writes.Select(w => (int)w.Descriptor.Id).SequenceEqual(new[] { RuntimeEventIds.SessionStart, RuntimeEventIds.SessionStop }),
            "Only enabled events reach EventWrite");
        check(provider.IsEnabled(EventLevel.Informational, RuntimeEventIds.SessionKeyword)
              && !provider.IsEnabled(EventLevel.Verbose, RuntimeEventIds.SamplingKeyword), "IsEnabled reflects the session level and keywords");

        var off = new RecordingEtwApi { SessionLevel = 0 };
        using var idle = new RuntimeEtwProvider(off);
        idle.SessionStart(1, "r", "v");
        idle.StackSample(1, 1, new ulong[] { 1 });
        check(off.Writes.Count == 0, "Nothing is written while no session listens");
    }

    private static void WriteFailures(Action<bool, string> check)
    {
        var api = new RecordingEtwApi { WriteResult = 234 };
        using var provider = new RuntimeEtwProvider(api);
        provider.Allocation(1, 2, 3, "t");
        check(provider.LastWriteStatus == 234, "EventWrite failure status is reported, not thrown");
        api.WriteThrows = true;
        provider.SessionStop(1, 0);
        check(provider.LastWriteStatus == uint.MaxValue, "An exception from the ETW layer never escapes a write");
    }

    private static void RealAdvapi(Action<bool, string> check)
    {
        using (var provider = new RuntimeEtwProvider())
        {
            check(provider.RegistrationStatus == 0 && provider.IsRegistered, "advapi32 EventRegister succeeds: " + provider.RegistrationStatus);
            check(provider.TraitsStatus == 0, "advapi32 EventSetInformation accepts the provider traits: " + provider.TraitsStatus);
        }

        // Force every event through the real EventWrite, even with no session attached.
        var forced = new ForcedEnableApi();
        using (var provider = new RuntimeEtwProvider(forced))
        {
            provider.SessionStart(9, "real", "1");
            check(provider.LastWriteStatus == 0, "advapi32 EventWrite SessionStart: " + provider.LastWriteStatus);
            provider.StackSample(9, 1, new ulong[] { 1, 2, 3 });
            check(provider.LastWriteStatus == 0, "advapi32 EventWrite StackSample: " + provider.LastWriteStatus);
            provider.Allocation(9, 0x10, 16, "real.Type");
            check(provider.LastWriteStatus == 0, "advapi32 EventWrite Allocation: " + provider.LastWriteStatus);
            provider.SessionStop(9, 0);
            check(provider.LastWriteStatus == 0, "advapi32 EventWrite SessionStop: " + provider.LastWriteStatus);
            check(forced.Writes == 4, "All four events reached advapi32");
        }
        check(forced.Unregistered == 0, "advapi32 EventUnregister succeeds");
    }

    private static void EventSourceEvents(Action<bool, string> check)
    {
        var log = RuntimeEventSource.Log;
        check(log.ConstructionException == null, "RuntimeEventSource metadata is valid: " + log.ConstructionException?.Message);
        check(log.Name == RuntimeEventSource.SourceName && log.Guid == RuntimeEventSource.SourceId, "RuntimeEventSource name and GUID");
        check(log.Settings.HasFlag(EventSourceSettings.EtwSelfDescribingEventFormat), "RuntimeEventSource writes self-describing events");
        check(!log.IsEnabled(), "RuntimeEventSource is off without a listener");

        using var listener = new RuntimeEventCollector();
        listener.EnableEvents(log, EventLevel.Verbose, RuntimeEventSource.Keywords.Session | RuntimeEventSource.Keywords.Sampling | RuntimeEventSource.Keywords.Allocation);
        IRuntimeEvents events = log;
        check(events.IsEnabled(EventLevel.Verbose, RuntimeEventIds.SamplingKeyword), "IRuntimeEvents.IsEnabled after a listener enables it");
        events.SessionStart(5, "Kotlin", "2.4.21");
        events.StackSample(5, 11, new ulong[] { 0xA, 0xB });
        events.Allocation(5, 0xFF00, 32, "app.Node");
        events.SessionStop(5, 1);
        events.StackSample(5, 11, new ulong[RuntimeEventIds.MaxStackFrames + 1]);

        var got = listener.Take();
        check(got.Select(e => e.EventId).SequenceEqual(new[] { 1, 3, 4, 2, 3 }), "EventListener receives each event once in order: "
            + string.Join(",", got.Select(e => e.EventId)));
        if (got.Count != 5) return;
        check(got.Select(e => e.EventName).SequenceEqual(new[] { "SessionStart", "StackSample", "Allocation", "SessionStop", "StackSample" }),
            "EventSource event names match the native provider");
        check(got[0].PayloadNames!.SequenceEqual(new[] { "SessionId", "RuntimeName", "RuntimeVersion" })
              && (ulong)got[0].Payload![0]! == 5 && (string)got[0].Payload![1]! == "Kotlin", "SessionStart payload names and values");
        check(got[0].Opcode == EventOpcode.Start && got[0].Task == RuntimeEventIds.SessionTask && got[0].Keywords.HasFlag(RuntimeEventIds.SessionKeyword)
              && got[0].Level == EventLevel.Informational, "SessionStart opcode, task, keyword and level");
        check(got[1].PayloadNames!.SequenceEqual(new[] { "SessionId", "ThreadId", "Frames" }) && (uint)got[1].Payload![1]! == 11
              && ((ulong[])got[1].Payload![2]!).SequenceEqual(new ulong[] { 0xA, 0xB }) && got[1].Level == EventLevel.Verbose, "StackSample payload");
        check(got[2].PayloadNames!.SequenceEqual(new[] { "SessionId", "Address", "Size", "TypeName" }) && (ulong)got[2].Payload![1]! == 0xFF00
              && (ulong)got[2].Payload![2]! == 32 && (string)got[2].Payload![3]! == "app.Node", "Allocation payload");
        check(got[3].Opcode == EventOpcode.Stop && (int)got[3].Payload![1]! == 1, "SessionStop payload and opcode");
        check(((ulong[])got[4].Payload![2]!).Length == RuntimeEventIds.MaxStackFrames, "EventSource clips deep stacks too");

        listener.DisableEvents(log);
        listener.EnableEvents(log, EventLevel.Informational, RuntimeEventSource.Keywords.Session);
        events.SessionStart(6, "r", "v");
        events.StackSample(6, 1, new ulong[] { 1 });
        events.Allocation(6, 1, 1, "t");
        check(listener.Take().Select(e => e.EventId).SequenceEqual(new[] { 1 }), "EventSource honors level and keyword filters");
        listener.DisableEvents(log);
    }

    private static void CheckEnvelope(Action<bool, string> check, RecordingEtwApi api, RecordedWrite write, string name, int id, EventLevel level,
        EventOpcode opcode, EventTask task, EventKeywords keyword, params (string Name, int Type)[] fields)
    {
        var d = write.Descriptor;
        check(write.Handle == RecordingEtwApi.Handle, name + " is written with the registration handle");
        check(d.Id == id && d.Version == 0 && d.Channel == 11 && d.Level == (byte)level && d.Opcode == (byte)opcode && d.Task == (ushort)task
              && d.Keyword == (ulong)keyword, name + " descriptor: id, TraceLogging channel, level, opcode, task, keyword");
        check(write.Segments.Select(s => s.Type).SequenceEqual(new[] { EtwSegmentType.ProviderMetadata, EtwSegmentType.EventMetadata, EtwSegmentType.Data }),
            name + " data descriptors: provider metadata, event metadata, payload");
        check(write.Segments[0].Bytes.SequenceEqual(api.Traits!), name + " provider metadata equals the registered traits");
        var metadata = ParseMetadata(write.Segments[1].Bytes);
        check(metadata.Name == name, name + " metadata carries the event name: " + metadata.Name);
        check(metadata.Fields.SequenceEqual(fields), name + " metadata fields: " + string.Join(",", metadata.Fields));
    }

    private static (string Name, List<(string Name, int Type)> Fields) ParseMetadata(byte[] bytes)
    {
        if (BitConverter.ToUInt16(bytes, 0) != bytes.Length) throw new Exception("Metadata size prefix does not match its length.");
        var pos = 2;
        if (bytes[pos++] != 0) throw new Exception("Unexpected event tags.");
        var name = ReadUtf8(bytes, ref pos);
        var fields = new List<(string, int)>();
        while (pos < bytes.Length) fields.Add((ReadUtf8(bytes, ref pos), bytes[pos++]));
        return (name, fields);
    }

    private static List<object> Decode(RecordedWrite write)
    {
        var fields = ParseMetadata(write.Segments[1].Bytes).Fields;
        var data = write.Segments[2].Bytes;
        var pos = 0;
        var values = new List<object>();
        foreach (var (_, type) in fields)
        {
            if ((type & 0x40) != 0)
            {
                var count = BitConverter.ToUInt16(data, pos); pos += 2;
                var array = new ulong[count];
                for (var i = 0; i < count; i++) { array[i] = BitConverter.ToUInt64(data, pos); pos += 8; }
                values.Add(array);
                continue;
            }
            switch (type)
            {
                case 1:
                    var start = pos;
                    while (BitConverter.ToUInt16(data, pos) != 0) pos += 2;
                    values.Add(Encoding.Unicode.GetString(data, start, pos - start));
                    pos += 2;
                    break;
                case 7: values.Add(BitConverter.ToInt32(data, pos)); pos += 4; break;
                case 8: values.Add(BitConverter.ToUInt32(data, pos)); pos += 4; break;
                case 10: case 21: values.Add(BitConverter.ToUInt64(data, pos)); pos += 8; break;
                default: throw new Exception("Unexpected field type " + type);
            }
        }
        if (pos != data.Length) throw new Exception("Payload has " + (data.Length - pos) + " trailing bytes.");
        return values;
    }

    private static string ReadUtf8(byte[] bytes, ref int pos)
    {
        var end = Array.IndexOf(bytes, (byte)0, pos);
        var text = Encoding.UTF8.GetString(bytes, pos, end - pos);
        pos = end + 1;
        return text;
    }

    private sealed class RecordedWrite
    {
        public RecordedWrite(ulong handle, EtwEventDescriptor descriptor, EtwSegment[] segments) { Handle = handle; Descriptor = descriptor; Segments = segments; }
        public ulong Handle { get; }
        public EtwEventDescriptor Descriptor { get; }
        public EtwSegment[] Segments { get; }
    }

    private sealed class RecordingEtwApi : IEtwApi
    {
        public const ulong Handle = 0x5EED;
        public uint RegisterResult;
        public uint WriteResult;
        public bool WriteThrows;
        public byte SessionLevel = (byte)EventLevel.Verbose;
        public ulong SessionKeywords = ulong.MaxValue;
        public int RegisterCalls;
        public Guid RegisteredId;
        public byte[]? Traits;
        public int UnregisterCalls;
        public ulong UnregisteredHandle;
        public readonly List<RecordedWrite> Writes = new List<RecordedWrite>();

        public uint Register(Guid providerId, out ulong handle)
        {
            RegisterCalls++;
            RegisteredId = providerId;
            handle = RegisterResult == 0 ? Handle : 0;
            return RegisterResult;
        }

        public uint SetProviderTraits(ulong handle, byte[] traits) { Traits = traits; return 0; }

        public bool IsEnabled(ulong handle, byte level, ulong keyword) =>
            SessionLevel != 0 && level <= SessionLevel && (keyword == 0 || (keyword & SessionKeywords) != 0);

        public uint Write(ulong handle, in EtwEventDescriptor descriptor, IReadOnlyList<EtwSegment> segments)
        {
            if (WriteThrows) throw new InvalidOperationException("ETW layer failure");
            Writes.Add(new RecordedWrite(handle, descriptor, segments.ToArray()));
            return WriteResult;
        }

        public uint Unregister(ulong handle) { UnregisterCalls++; UnregisteredHandle = handle; return 0; }
    }

    private sealed class ForcedEnableApi : IEtwApi
    {
        public int Writes;
        public uint Unregistered = uint.MaxValue;
        public uint Register(Guid providerId, out ulong handle) => AdvapiEtwApi.Instance.Register(providerId, out handle);
        public uint SetProviderTraits(ulong handle, byte[] traits) => AdvapiEtwApi.Instance.SetProviderTraits(handle, traits);
        public bool IsEnabled(ulong handle, byte level, ulong keyword) => true;
        public uint Write(ulong handle, in EtwEventDescriptor descriptor, IReadOnlyList<EtwSegment> segments)
        {
            Writes++;
            return AdvapiEtwApi.Instance.Write(handle, descriptor, segments);
        }
        public uint Unregister(ulong handle) => Unregistered = AdvapiEtwApi.Instance.Unregister(handle);
    }

    private sealed class RuntimeEventCollector : EventListener
    {
        private readonly List<EventWrittenEventArgs> events = new List<EventWrittenEventArgs>();

        public List<EventWrittenEventArgs> Take()
        {
            lock (events) { var copy = events.ToList(); events.Clear(); return copy; }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != RuntimeEventSource.SourceName || eventData.EventId <= 0) return;
            lock (events) events.Add(eventData);
        }
    }
}
