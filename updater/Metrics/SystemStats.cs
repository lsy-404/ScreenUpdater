using System.Management;
using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

/// <param name="DownloadBps">Bytes per second received on the busiest network interface.</param>
/// <param name="MemoryMhz">Configured speed of the installed memory modules.</param>
/// <param name="MemStandbyGb">Cached pages on the standby lists, which Windows hands out again as soon as memory runs short.</param>
/// <param name="DiskUsedGb">Space in use, summed over the fixed drives.</param>
/// <param name="DiskFreeGb">Space this user can still write, summed over the fixed drives.</param>
/// <param name="DiskReadBps">Bytes per second read from all physical disks.</param>
public sealed record SystemSample(
    int Processes, int Threads, int Handles, double DownloadBps, double UploadBps, int? MemoryMhz, float MemStandbyGb,
    float DiskUsedGb, float DiskTotalGb, float DiskFreeGb, double DiskReadBps, double DiskWriteBps);

/// <summary>Process, thread and handle totals, network and disk throughput, disk usage and memory speed.</summary>
public sealed class SystemStats : IDisposable
{
    readonly PdhQuery _pdh = new();
    readonly IntPtr _diskRead;
    readonly IntPtr _diskWrite;
    readonly IntPtr _netReceived;
    readonly IntPtr _netSent;
    readonly IntPtr[] _standby;
    readonly int? _memoryMhz = ReadMemorySpeed();

    public SystemStats()
    {
        _netReceived = _pdh.Add(@"\Network Interface(*)\Bytes Received/sec");
        _netSent = _pdh.Add(@"\Network Interface(*)\Bytes Sent/sec");
        _diskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _standby = [.. new[] { "Core", "Normal Priority", "Reserve" }
            .Select(list => _pdh.Add($@"\Memory\Standby Cache {list} Bytes"))];
        _pdh.Collect();
    }

    public void Dispose() => _pdh.Dispose();

    public SystemSample Sample()
    {
        _pdh.Collect();
        long diskTotal = 0, diskFree = 0, diskAvailable = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            diskTotal += drive.TotalSize;
            diskFree += drive.TotalFreeSpace;
            diskAvailable += drive.AvailableFreeSpace;
        }

        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        GetPerformanceInfo(ref info, info.Size);

        var sent = _pdh.Values(_netSent);
        double down = 0, up = 0;
        // Adapters can repeat each other's traffic, so take the busiest one rather than the sum.
        foreach (var (adapter, adapterDown) in _pdh.Values(_netReceived))
        {
            double adapterUp = sent.GetValueOrDefault(adapter);
            if (adapterDown + adapterUp > down + up)
            {
                down = adapterDown;
                up = adapterUp;
            }
        }

        return new SystemSample(
            (int)info.ProcessCount, (int)info.ThreadCount, (int)info.HandleCount,
            Math.Max(down, 0), Math.Max(up, 0), _memoryMhz,
            (float)(_standby.Sum(c => _pdh.Value(c) ?? 0) / 1073741824),
            (diskTotal - diskFree) / 1073741824f, diskTotal / 1073741824f, diskAvailable / 1073741824f,
            _pdh.Value(_diskRead) ?? 0, _pdh.Value(_diskWrite) ?? 0);
    }

    static int? ReadMemorySpeed()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            foreach (var module in searcher.Get())
            {
                if (module["ConfiguredClockSpeed"] is uint speed && speed > 0) return (int)speed;
            }
        }
        catch (ManagementException)
        {
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll")]
    static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
