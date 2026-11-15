using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>Heap figures a language runtime reports about itself (JVM through jstat, Dart through its VM service).</summary>
    public sealed class RuntimeHeap
    {
        public RuntimeHeap(string runtime, long used, long capacity, long? collections = null, double? collectionSeconds = null, long? external = null)
        {
            Runtime = runtime; Used = used; Capacity = capacity; Collections = collections; CollectionSeconds = collectionSeconds; External = external;
        }
        public string Runtime { get; }
        public long Used { get; }
        public long Capacity { get; }
        /// <summary>Garbage collections so far, when the runtime counts them.</summary>
        public long? Collections { get; }
        public double? CollectionSeconds { get; }
        public long? External { get; }
    }

    public sealed class UsageSample
    {
        public UsageSample(DateTime time, double? cpuPercent, long workingSet, long privateBytes, int processes, RuntimeHeap? heap = null, bool broken = false)
        {
            Time = time; CpuPercent = cpuPercent; WorkingSet = workingSet; PrivateBytes = privateBytes; Processes = processes; Heap = heap; Broken = broken;
        }
        public DateTime Time { get; }
        /// <summary>Share of all logical processors, 0-100; null for the first reading, which has nothing to compare with.</summary>
        public double? CpuPercent { get; }
        public long WorkingSet { get; }
        public long PrivateBytes { get; }
        public int Processes { get; }
        public RuntimeHeap? Heap { get; }
        /// <summary>The debugger was in break mode when the sample was taken.</summary>
        public bool Broken { get; }
        internal UsageSample With(RuntimeHeap? heap, bool broken) => new UsageSample(Time, CpuPercent, WorkingSet, PrivateBytes, Processes, heap, broken);
    }

    /// <summary>Turns cumulative process CPU times into a CPU % between two readings of the same process set.</summary>
    public sealed class UsageCalculator
    {
        private readonly int processors;
        private Dictionary<(int, long), long>? previous;
        private DateTime previousTime;

        public UsageCalculator(int processors) { this.processors = Math.Max(1, processors); }

        public UsageSample Next(DateTime time, IReadOnlyList<ProcessCounters> counters)
        {
            double? cpu = null;
            if (previous != null && time > previousTime)
            {
                long used = 0;
                // A process that appeared since the last reading has no baseline; counting its whole CPU time would
                // show a spike for work done before it was seen.
                foreach (var c in counters)
                    if (previous.TryGetValue((c.Pid, c.CreationTime), out var before)) used += Math.Max(0, c.CpuTicks - before);
                var available = (double)(time - previousTime).Ticks * processors;
                cpu = Math.Min(100, Math.Max(0, 100 * used / available));
            }
            previous = counters.ToDictionary(c => (c.Pid, c.CreationTime), c => c.CpuTicks);
            previousTime = time;
            return new UsageSample(time, cpu, counters.Sum(c => c.WorkingSet), counters.Sum(c => c.PrivateBytes), counters.Count);
        }
    }

    public enum DiagnosticsEventKind { Started, Break, Continue, GarbageCollection, Snapshot, Exited, Note }

    public sealed class DiagnosticsEvent
    {
        public DiagnosticsEvent(DateTime time, DiagnosticsEventKind kind, string text) { Time = time; Kind = kind; Text = text; }
        public DateTime Time { get; }
        public DiagnosticsEventKind Kind { get; }
        public string Text { get; }
        public override string ToString() => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Kind + "  " + Text;
    }

    public sealed class HeapClassEntry
    {
        public HeapClassEntry(string name, long instances, long bytes) { Name = name; Instances = instances; Bytes = bytes; }
        public string Name { get; }
        public long Instances { get; }
        public long Bytes { get; }
    }

    public sealed class HeapClassDelta
    {
        public HeapClassDelta(string name, long instances, long bytes, long instancesChange, long bytesChange)
        {
            Name = name; Instances = instances; Bytes = bytes; InstancesChange = instancesChange; BytesChange = bytesChange;
        }
        public string Name { get; }
        public long Instances { get; }
        public long Bytes { get; }
        public long InstancesChange { get; }
        public long BytesChange { get; }
    }

    /// <summary>A heap snapshot: classes by size, as the runtime counts them (empty when only process counters exist).</summary>
    public sealed class HeapSnapshot
    {
        public HeapSnapshot(DateTime time, string source, IReadOnlyList<HeapClassEntry> classes, long? totalBytes = null, long? totalInstances = null,
            long workingSet = 0, long privateBytes = 0)
        {
            Time = time; Source = source;
            Classes = classes.OrderByDescending(c => c.Bytes).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            TotalBytes = totalBytes ?? classes.Sum(c => c.Bytes);
            TotalInstances = totalInstances ?? classes.Sum(c => c.Instances);
            WorkingSet = workingSet; PrivateBytes = privateBytes;
        }
        public int Number { get; internal set; }
        public DateTime Time { get; }
        public string Source { get; }
        public IReadOnlyList<HeapClassEntry> Classes { get; }
        public long TotalBytes { get; }
        public long TotalInstances { get; }
        public long WorkingSet { get; internal set; }
        public long PrivateBytes { get; internal set; }

        /// <summary>Classes ordered by how much their size changed since <paramref name="previous"/> (largest growth first).</summary>
        public IReadOnlyList<HeapClassDelta> Compare(HeapSnapshot? previous)
        {
            var before = previous?.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => (g.Sum(c => c.Instances), g.Sum(c => c.Bytes)))
                ?? new Dictionary<string, (long, long)>();
            var now = Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => (g.Sum(c => c.Instances), g.Sum(c => c.Bytes)));
            return now.Keys.Union(before.Keys)
                .Select(name =>
                {
                    now.TryGetValue(name, out var n);
                    before.TryGetValue(name, out var b);
                    return new HeapClassDelta(name, n.Item1, n.Item2, n.Item1 - b.Item1, n.Item2 - b.Item2);
                })
                .OrderByDescending(d => Math.Abs(d.BytesChange)).ThenByDescending(d => d.Bytes).ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// What a diagnostics session recorded: samples (newest kept up to the capacity), events and snapshots. Thread-safe;
    /// <see cref="Changed"/> fires on the writer's thread.
    /// </summary>
    public sealed class DiagnosticsTimeline
    {
        private readonly object gate = new object();
        private readonly List<UsageSample> samples = new List<UsageSample>();
        private readonly List<DiagnosticsEvent> events = new List<DiagnosticsEvent>();
        private readonly List<HeapSnapshot> snapshots = new List<HeapSnapshot>();
        private readonly int capacity;

        public DiagnosticsTimeline(string title, int capacity = 3600) { Title = title; this.capacity = Math.Max(2, capacity); }

        public string Title { get; }
        public event Action? Changed;

        public IReadOnlyList<UsageSample> Samples { get { lock (gate) return samples.ToList(); } }
        public IReadOnlyList<DiagnosticsEvent> Events { get { lock (gate) return events.ToList(); } }
        public IReadOnlyList<HeapSnapshot> Snapshots { get { lock (gate) return snapshots.ToList(); } }
        public UsageSample? Latest { get { lock (gate) return samples.Count == 0 ? null : samples[samples.Count - 1]; } }

        public void Add(UsageSample sample)
        {
            lock (gate)
            {
                samples.Add(sample);
                if (samples.Count > capacity) samples.RemoveRange(0, samples.Count - capacity);
            }
            Changed?.Invoke();
        }

        public void Add(DiagnosticsEvent e)
        {
            lock (gate)
            {
                events.Add(e);
                if (events.Count > 2000) events.RemoveRange(0, events.Count - 2000);
            }
            Changed?.Invoke();
        }

        public HeapSnapshot Add(HeapSnapshot snapshot)
        {
            lock (gate)
            {
                snapshot.Number = snapshots.Count + 1;
                snapshots.Add(snapshot);
            }
            Changed?.Invoke();
            return snapshot;
        }

        public static string FormatBytes(long bytes)
        {
            var value = Math.Abs((double)bytes);
            var sign = bytes < 0 ? "-" : "";
            if (value >= 1L << 30) return sign + (value / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (value >= 1L << 20) return sign + (value / (1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (value >= 1L << 10) return sign + (value / (1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return sign + value.ToString("0", CultureInfo.InvariantCulture) + " B";
        }
    }
}
