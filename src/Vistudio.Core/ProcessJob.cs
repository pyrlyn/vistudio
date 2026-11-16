using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vistudio.Core
{
    // Closing this handle (including when the VS process exits) terminates members.
    internal sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        internal ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits)))
            {
                var error = new Win32Exception();
                handle.Dispose();
                throw error;
            }
        }
        internal void Assign(IntPtr process)
        {
            if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception();
        }
        internal void Terminate()
        {
            if (!TerminateJobObject(handle, 1)) throw new Win32Exception();
        }
        public void Dispose() => handle.Dispose();

        /// <summary>The ids of the processes currently in the job (the started process and all its descendants).</summary>
        internal int[] ProcessIds()
        {
            if (handle.IsClosed || handle.IsInvalid) return Array.Empty<int>();
            const int capacity = 4096;
            var size = 8 + IntPtr.Size * capacity;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTR ids (aligned to pointer size).
                if (!QueryInformationJobObject(handle, 3, buffer, (uint)size, IntPtr.Zero)) return Array.Empty<int>();
                var count = Marshal.ReadInt32(buffer, 4);
                var ids = new int[count];
                for (var i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
                return ids;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
