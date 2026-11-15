using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>
    /// Samples a debuggee once per interval: CPU % and memory of its processes, the runtime's heap when a probe exists,
    /// garbage collections, break/continue marks and snapshots, into a <see cref="DiagnosticsTimeline"/>. It ends by
    /// itself when every watched process has exited.
    /// </summary>
    public sealed class DiagnosticsMonitor : IDisposable
    {
        private readonly Func<IReadOnlyList<int>> processes;
        private readonly IRuntimeProbe? runtime;
        private readonly IProcessCounterSource counters;
        private readonly TimeSpan interval;
        private readonly Func<DateTime> clock;
        private readonly UsageCalculator calculator = new UsageCalculator(Environment.ProcessorCount);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private Task? loop;
        private RuntimeHeap? lastHeap;
        private bool runtimeFailed, seenProcess, ended;
        private volatile bool broken;

        public DiagnosticsMonitor(string title, Func<IReadOnlyList<int>> processes, IRuntimeProbe? runtime,
            IProcessCounterSource? counters = null, TimeSpan? interval = null, Func<DateTime>? clock = null)
        {
            Timeline = new DiagnosticsTimeline(title);
            this.processes = processes;
            this.runtime = runtime;
            this.counters = counters ?? Win32ProcessCounters.Instance;
            this.interval = interval ?? TimeSpan.FromSeconds(1);
            this.clock = clock ?? (() => DateTime.Now);
        }

        public DiagnosticsTimeline Timeline { get; }
        public bool HasRuntime => runtime != null;
        public bool IsRunning => loop != null && !ended;
        public event Action? Ended;

        public void Start(string description)
        {
            if (loop != null) return;
            Note(DiagnosticsEventKind.Started, description);
            loop = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!await SampleAsync(stop.Token).ConfigureAwait(false)) { Finish("The program exited."); return; }
                    await Task.Delay(interval, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Finish("Sampling stopped: " + e.Message); }
        }

        /// <summary>Takes one sample; false once the watched processes are gone after having been seen.</summary>
        public async Task<bool> SampleAsync(CancellationToken token)
        {
            var read = new List<ProcessCounters>();
            foreach (var pid in processes())
                if (counters.TryRead(pid, out var c)) read.Add(c);
            if (read.Count == 0) return !seenProcess;
            seenProcess = true;
            var sample = calculator.Next(clock(), read);
            var heap = await ReadRuntimeAsync(token).ConfigureAwait(false);
            if (heap != null) ReportCollections(heap);
            Timeline.Add(sample.With(heap, broken));
            return true;
        }

        private async Task<RuntimeHeap?> ReadRuntimeAsync(CancellationToken token)
        {
            if (runtime == null) return null;
            try
            {
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(3));
                    var heap = await runtime.ReadAsync(limit.Token).ConfigureAwait(false);
                    runtimeFailed = false;
                    return heap;
                }
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                // A runtime that stops answering (exiting, or a VM service restart on hot restart) is reported once,
                // and process counters keep coming.
                if (!runtimeFailed) Note(DiagnosticsEventKind.Note, runtime.Name + " data unavailable: " + e.Message);
                runtimeFailed = true;
                return null;
            }
        }

        private void ReportCollections(RuntimeHeap heap)
        {
            var previous = lastHeap;
            lastHeap = heap;
            if (previous == null) return;
            if (heap.Collections is long count && previous.Collections is long before && count > before)
            {
                var pause = heap.CollectionSeconds - previous.CollectionSeconds;
                Note(DiagnosticsEventKind.GarbageCollection, (count - before) + " garbage collection" + (count - before == 1 ? "" : "s") +
                    (pause is double p && p > 0 ? " (" + (p * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms)" : "") +
                    ", heap " + DiagnosticsTimeline.FormatBytes(previous.Used) + " -> " + DiagnosticsTimeline.FormatBytes(heap.Used));
            }
            else if (heap.Collections == null && previous.Used - heap.Used >= Math.Max(1L << 20, previous.Used / 10))
            {
                // The Dart VM service reports no collection count; a large drop in used heap is a collection.
                Note(DiagnosticsEventKind.GarbageCollection, heap.Runtime + " shrank " + DiagnosticsTimeline.FormatBytes(previous.Used) + " -> " +
                    DiagnosticsTimeline.FormatBytes(heap.Used) + " (garbage collection)");
            }
        }

        /// <summary>Debugger break mode on or off; samples taken while broken are marked.</summary>
        public void SetBroken(bool value)
        {
            if (broken == value || ended) return;
            broken = value;
            Note(value ? DiagnosticsEventKind.Break : DiagnosticsEventKind.Continue, value ? "Debugger break" : "Continue");
        }

        /// <summary>Classes on the heap from the runtime, or a counters-only snapshot when the program has no runtime probe.</summary>
        public async Task<HeapSnapshot> TakeSnapshotAsync(CancellationToken token)
        {
            HeapSnapshot snapshot;
            if (runtime != null) snapshot = await runtime.SnapshotAsync(token).ConfigureAwait(false);
            else snapshot = new HeapSnapshot(clock(), "process counters", Array.Empty<HeapClassEntry>());
            var latest = Timeline.Latest;
            snapshot.WorkingSet = latest?.WorkingSet ?? 0;
            snapshot.PrivateBytes = latest?.PrivateBytes ?? 0;
            Timeline.Add(snapshot);
            Note(DiagnosticsEventKind.Snapshot, "Snapshot #" + snapshot.Number + ": " +
                (snapshot.Classes.Count == 0 ? "private bytes " + DiagnosticsTimeline.FormatBytes(snapshot.PrivateBytes)
                    : snapshot.Classes.Count + " classes, " + DiagnosticsTimeline.FormatBytes(snapshot.TotalBytes) + " in " + snapshot.TotalInstances.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " objects"));
            return snapshot;
        }

        public void Note(DiagnosticsEventKind kind, string text) => Timeline.Add(new DiagnosticsEvent(clock(), kind, text));

        public void Stop(string reason)
        {
            stop.Cancel();
            Finish(reason);
        }

        private void Finish(string reason)
        {
            lock (stop)
            {
                if (ended) return;
                ended = true;
            }
            Note(DiagnosticsEventKind.Exited, reason);
            runtime?.Dispose();
            Ended?.Invoke();
        }

        // The token source is not disposed: the sampling loop may still be between its last check and Task.Delay.
        public void Dispose() => Stop("Session closed.");
    }
}
