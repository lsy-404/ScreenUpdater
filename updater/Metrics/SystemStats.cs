using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

/// <param name="DownloadBps">Bytes per second received on the busiest network interface.</param>
/// <param name="MemoryMhz">Configured speed of the installed memory modules.</param>
/// <param name="DiskUsedGb">Space in use, summed over the fixed drives.</param>
/// <param name="DiskReadBps">Bytes per second read from all physical disks.</param>
public sealed record SystemSample(
    int Processes, int Threads, int Handles, double DownloadBps, double UploadBps, int? MemoryMhz,
    float DiskUsedGb, float DiskTotalGb, double DiskReadBps, double DiskWriteBps);

/// <summary>Process, thread and handle totals, network and disk throughput, disk usage and memory speed.</summary>
public sealed class SystemStats : IDisposable
{
    readonly PdhQuery _pdh = new();
    readonly IntPtr _diskRead;
    readonly IntPtr _diskWrite;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly int? _memoryMhz = ReadMemorySpeed();
    Dictionary<string, (long Received, long Sent)> _previous = [];
    double _previousTime;

    public SystemStats()
    {
        _diskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _pdh.Collect();
    }

    public void Dispose() => _pdh.Dispose();

    public SystemSample Sample()
    {
        _pdh.Collect();
        long diskTotal = 0, diskFree = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            diskTotal += drive.TotalSize;
            diskFree += drive.TotalFreeSpace;
        }

        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        GetPerformanceInfo(ref info, info.Size);

        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _previousTime;
        var current = new Dictionary<string, (long Received, long Sent)>();
        double down = 0, up = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var stats = nic.GetIPStatistics();
            current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            if (!_previous.TryGetValue(nic.Id, out var before) || elapsed <= 0) continue;

            // Virtual switches and tunnels repeat the physical adapter's traffic, so take the busiest interface rather than the sum.
            double nicDown = (stats.BytesReceived - before.Received) / elapsed;
            double nicUp = (stats.BytesSent - before.Sent) / elapsed;
            if (nicDown + nicUp > down + up)
            {
                down = nicDown;
                up = nicUp;
            }
        }

        _previous = current;
        _previousTime = now;

        return new SystemSample(
            (int)info.ProcessCount, (int)info.ThreadCount, (int)info.HandleCount,
            Math.Max(down, 0), Math.Max(up, 0), _memoryMhz,
            (diskTotal - diskFree) / 1073741824f, diskTotal / 1073741824f,
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
