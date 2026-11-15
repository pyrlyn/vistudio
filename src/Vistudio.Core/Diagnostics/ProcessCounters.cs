using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Vistudio.Core.Diagnostics
{
    /// <summary>One reading of a process: CPU time used so far and memory now.</summary>
    public readonly struct ProcessCounters
    {
        public ProcessCounters(int pid, long creationTime, long cpuTicks, long workingSet, long privateBytes)
        {
            Pid = pid; CreationTime = creationTime; CpuTicks = cpuTicks; WorkingSet = workingSet; PrivateBytes = privateBytes;
        }
        public int Pid { get; }
        /// <summary>FILETIME of process creation; together with <see cref="Pid"/> it tells a reused process id apart.</summary>
        public long CreationTime { get; }
        /// <summary>User plus kernel time in 100 ns ticks.</summary>
        public long CpuTicks { get; }
        public long WorkingSet { get; }
        public long PrivateBytes { get; }
    }

    /// <summary>A row of the process table.</summary>
    public readonly struct ProcessEntry
    {
        public ProcessEntry(int pid, int parentPid, string imageName) { Pid = pid; ParentPid = parentPid; ImageName = imageName; }
        public int Pid { get; }
        public int ParentPid { get; }
        public string ImageName { get; }
    }

    public interface IProcessCounterSource
    {
        /// <summary>False when the process has exited or cannot be opened.</summary>
        bool TryRead(int pid, out ProcessCounters counters);
    }

    public static class ProcessTree
    {
        /// <summary>
        /// The root and every process started below it. A child whose parent id belongs to the tree but which was created
        /// before that parent is a reused process id, not a child, and is left out.
        /// </summary>
        public static IReadOnlyList<int> WithDescendants(int root, IReadOnlyList<ProcessEntry> table, Func<int, long?> creationTime)
        {
            var result = new List<int>();
            var rootCreated = creationTime(root);
            if (rootCreated == null) return result;
            result.Add(root);
            var created = new Dictionary<int, long> { [root] = rootCreated.Value };
            for (var i = 0; i < result.Count; i++)
            {
                var parent = result[i];
                foreach (var entry in table)
                {
                    if (entry.ParentPid != parent || entry.Pid == parent || created.ContainsKey(entry.Pid)) continue;
                    var childCreated = creationTime(entry.Pid);
                    if (childCreated == null || childCreated.Value < created[parent]) continue;
                    created[entry.Pid] = childCreated.Value;
                    result.Add(entry.Pid);
                }
            }
            return result;
        }
    }

    public static class ProgramProcesses
    {
        /// <summary>Gradle's own processes when it runs a program (<c>cmd /c gradlew.bat --no-daemon</c>, its JVMs).</summary>
        public static readonly IReadOnlyCollection<string> GradleImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cmd.exe", "conhost.exe", "java.exe", "javaw.exe" };

        /// <summary>
        /// The processes of <paramref name="tree"/> whose image is not one of <paramref name="toolImages"/>: what a Gradle
        /// run task started (Node for Kotlin/JS and wasmJs, the Kotlin/Native executable), without the build's own JVMs.
        /// </summary>
        public static IReadOnlyList<int> Without(IReadOnlyList<int> tree, IReadOnlyList<ProcessEntry> table, IReadOnlyCollection<string> toolImages)
        {
            var images = table.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First().ImageName);
            return tree.Where(pid => !(images.TryGetValue(pid, out var image) && toolImages.Contains(image))).ToList();
        }
    }

    /// <summary>Win32 process counters; needs only PROCESS_QUERY_LIMITED_INFORMATION, so it works for any process of the user.</summary>
    public sealed class Win32ProcessCounters : IProcessCounterSource
    {
        public static readonly Win32ProcessCounters Instance = new Win32ProcessCounters();
        private const uint QueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;

        public bool TryRead(int pid, out ProcessCounters counters)
        {
            counters = default;
            var handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (!GetExitCodeProcess(handle, out var exit) || exit != StillActive) return false;
                if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user)) return false;
                var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
                if (!K32GetProcessMemoryInfo(handle, ref memory, memory.Size)) return false;
                counters = new ProcessCounters(pid, creation, kernel + user, (long)memory.WorkingSetSize.ToUInt64(), (long)memory.PrivateUsage.ToUInt64());
                return true;
            }
            finally { CloseHandle(handle); }
        }

        public long? CreationTime(int pid)
        {
            var handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;
            try { return GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : (long?)null; }
            finally { CloseHandle(handle); }
        }

        /// <summary>Full path of the process image, or null.</summary>
        public static string? ImagePath(int pid)
        {
            var handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        public IReadOnlyList<ProcessEntry> Snapshot()
        {
            var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
            if (snapshot == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var result = new List<ProcessEntry>();
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    result.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
                return result;
            }
            finally { CloseHandle(snapshot); }
        }

        /// <summary>The root and its descendants that are still running.</summary>
        public IReadOnlyList<int> Tree(int root) => ProcessTree.WithDescendants(root, Snapshot(), CreationTime);

        /// <summary>The descendants of <paramref name="root"/> that are not Gradle's own processes.</summary>
        public IReadOnlyList<int> ProgramUnderGradle(int root)
        {
            var table = Snapshot();
            return ProgramProcesses.Without(ProcessTree.WithDescendants(root, table, CreationTime), table, ProgramProcesses.GradleImages);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    }
}
