using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

/// <summary>A job object that terminates its processes when this process exits.</summary>
public sealed class ChildProcessJob : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadTransfer, WriteTransfer, OtherTransfer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInfo
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInfo info, int length);

    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    readonly IntPtr _handle;

    public ChildProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimitInfo { Basic = { LimitFlags = KillOnJobClose } };
        SetInformationJobObject(_handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
    }

    public void Add(Process process) => AssignProcessToJobObject(_handle, process.Handle);

    public void Dispose() => CloseHandle(_handle);
}
