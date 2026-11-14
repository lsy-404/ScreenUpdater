using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ScreenUpdater.Metrics;

/// <param name="Load">Busiest engine of the adapter, as Task Manager reports utilisation.</param>
/// <param name="Engine">Type of that engine, e.g. "3D" or "VideoEncode".</param>
public sealed record GpuSample(
    string Name, float? Load, string? Engine, float? Temp, float? VramUsedMb, float VramTotalMb);

public sealed record HardwareSample(
    float? CpuLoad,
    IReadOnlyList<GpuSample> Gpus,
    float MemLoad, float MemUsedGb, float MemTotalGb);

/// <summary>
/// System metrics from the sources Task Manager uses: performance counters for CPU and GPU load,
/// and the display kernel's adapter performance data for GPU temperature.
/// </summary>
public sealed partial class Sensors : IDisposable
{
    public const int GpuCount = 2;

    sealed class Gpu
    {
        public required string Name;
        public required string LuidKey;
        public required float TotalMb;
        public uint Handle;
    }

    readonly PerformanceCounter _cpu = new("Processor Information", "% Processor Utility", "_Total");
    readonly PerformanceCounterCategory _engines = new("GPU Engine");
    readonly PerformanceCounterCategory _adapterMemory = new("GPU Adapter Memory");
    readonly Gpu[] _gpus;
    Dictionary<string, CounterSample> _previousEngines = [];

    public Sensors()
    {
        _cpu.NextValue();

        var active = _adapterMemory.GetInstanceNames()
            .Select(LuidKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The adapters with the most dedicated memory, which leaves out integrated graphics.
        var gpus = new List<Gpu>();
        using (var directX = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
        {
            foreach (var name in directX?.GetSubKeyNames() ?? [])
            {
                using var key = directX!.OpenSubKey(name);
                if (key?.GetValue("Description") is not string description) continue;
                if (key.GetValue("AdapterLuid") is not long luid || key.GetValue("DedicatedVideoMemory") is not long memory) continue;

                var luidKey = $"0x{luid >> 32:X8}_0x{luid & 0xFFFFFFFF:X8}";
                if (!active.Contains(luidKey)) continue;

                var gpu = new Gpu { Name = description, LuidKey = luidKey, TotalMb = memory / 1048576f };
                var open = new OpenAdapterFromLuid { LowPart = (uint)luid, HighPart = (int)(luid >> 32) };
                if (D3DKMTOpenAdapterFromLuid(ref open) == 0) gpu.Handle = open.Adapter;
                gpus.Add(gpu);
            }
        }
        _gpus = [.. gpus.OrderByDescending(g => g.TotalMb).Take(GpuCount)];
        foreach (var gpu in gpus.Except(_gpus)) Close(gpu);
    }

    public HardwareSample Sample()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        float totalGb = memory.TotalPhysical / 1073741824f;
        float usedGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824f;

        var engineLoad = ReadEngineLoad();
        var dedicated = ReadDedicatedUsage();

        var gpus = new List<GpuSample>(_gpus.Length);
        foreach (var gpu in _gpus)
        {
            float? load = null;
            string? engine = null;
            foreach (var ((luid, _, type), value) in engineLoad)
            {
                if (!luid.Equals(gpu.LuidKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (load is null || value > load)
                {
                    load = value;
                    engine = type;
                }
            }

            gpus.Add(new GpuSample(
                gpu.Name,
                load is { } l ? Math.Min(l, 100) : null,
                load > 0 ? engine : null,
                ReadTemperature(gpu),
                dedicated.TryGetValue(gpu.LuidKey, out long bytes) ? bytes / 1048576f : null,
                gpu.TotalMb));
        }

        return new HardwareSample(Math.Min(_cpu.NextValue(), 100), gpus, memory.MemoryLoad, usedGb, totalGb);
    }

    /// <summary>Utilisation per (adapter, engine index, engine type), summed over processes.</summary>
    Dictionary<(string Luid, string Index, string Type), float> ReadEngineLoad()
    {
        var result = new Dictionary<(string, string, string), float>();
        var current = new Dictionary<string, CounterSample>();

        var data = _engines.ReadCategory();
        if (data["Utilization Percentage"] is { } instances)
        {
            foreach (InstanceData instance in instances.Values)
            {
                current[instance.InstanceName] = instance.Sample;
                if (!_previousEngines.TryGetValue(instance.InstanceName, out var previous)) continue;

                var match = EngineInstance().Match(instance.InstanceName);
                if (!match.Success) continue;

                var key = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                result[key] = result.GetValueOrDefault(key) + CounterSample.Calculate(previous, instance.Sample);
            }
        }

        _previousEngines = current;
        return result;
    }

    Dictionary<string, long> ReadDedicatedUsage()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (_adapterMemory.ReadCategory()["Dedicated Usage"] is { } instances)
        {
            foreach (InstanceData instance in instances.Values)
                result[LuidKey(instance.InstanceName)] = instance.RawValue;
        }
        return result;
    }

    static float? ReadTemperature(Gpu gpu)
    {
        if (gpu.Handle == 0) return null;

        var perf = new AdapterPerfData();
        int size = Marshal.SizeOf<AdapterPerfData>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(perf, buffer, false);
            var query = new QueryAdapterInfo
            {
                Adapter = gpu.Handle,
                Type = AdapterPerfDataType,
                Data = buffer,
                DataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
            perf = Marshal.PtrToStructure<AdapterPerfData>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Deci-degrees Celsius; 0 when the driver does not report it.
        return perf.Temperature > 0 ? perf.Temperature / 10f : null;
    }

    /// <summary>"luid_0x00000000_0x0001D52A_phys_0" → "0x00000000_0x0001D52A".</summary>
    static string LuidKey(string instanceName) => instanceName.Length >= 26 ? instanceName.Substring(5, 21) : instanceName;

    [GeneratedRegex(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_\d+_eng_(\d+)_engtype_(.+)$")]
    private static partial Regex EngineInstance();

    static void Close(Gpu gpu)
    {
        if (gpu.Handle == 0) return;
        var close = new CloseAdapter { Adapter = gpu.Handle };
        D3DKMTCloseAdapter(ref close);
        gpu.Handle = 0;
    }

    public void Dispose()
    {
        foreach (var gpu in _gpus) Close(gpu);
        _cpu.Dispose();
    }

    const int AdapterPerfDataType = 62; // KMTQAITYPE_ADAPTERPERFDATA

    [StructLayout(LayoutKind.Sequential)]
    struct OpenAdapterFromLuid
    {
        public uint LowPart;
        public int HighPart;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint DataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("gdi32.dll")]
    static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);

    [DllImport("gdi32.dll")]
    static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    static extern int D3DKMTCloseAdapter(ref CloseAdapter close);

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
