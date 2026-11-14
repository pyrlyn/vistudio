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
