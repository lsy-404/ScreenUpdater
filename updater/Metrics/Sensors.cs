using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ScreenUpdater.Metrics;

/// <param name="Type">The driver's name for the engine, e.g. "3D", "Video Codec 0" or "VideoEncode".</param>
public sealed record EngineLoad(string Type, int Index, float Load);

/// <param name="Load">Busiest engine of the adapter, as Task Manager reports utilisation.</param>
/// <param name="Engines">Utilisation per engine type of this adapter, in engine order.</param>
/// <param name="PowerW">Measured by the driver, or else the adapter's power limit scaled by the share the display kernel reports.</param>
public sealed record GpuSample(
    string Name, float? Load, IReadOnlyList<EngineLoad> Engines, float? Temp, float? ClockMhz,
    float? PowerW, float? VramUsedGb, float VramTotalGb);

public sealed record HardwareSample(
    float CpuLoad, float CpuGhz, float? CpuTemp, float? CpuPower,
    IReadOnlyList<GpuSample> Gpus,
    float MemLoad, float MemUsedGb, float MemTotalGb);

/// <summary>
/// System metrics from the sources Task Manager uses: PDH performance counters for CPU and GPU load,
/// and the display kernel's adapter performance data for GPU temperature.
/// </summary>
public sealed partial class Sensors : IDisposable
{
    public const int GpuCount = 2;

    sealed class Gpu
    {
        public required string Name;
        public required string LuidKey;
        public required float TotalGb;
        public float? PowerLimitW;
        public uint Handle;
    }

    readonly PdhQuery _pdh = new();
    readonly IntPtr _cpu;
    readonly IntPtr _cpuPerformance;
    readonly IntPtr _cpuBaseMhz;
    readonly IntPtr _engines;
    readonly IntPtr _adapterMemory;
    readonly DriverSensors _driver = new();
    readonly Gpu[] _gpus;

    public Sensors()
    {
        _cpu = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        _cpuPerformance = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _cpuBaseMhz = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _engines = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _adapterMemory = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _pdh.Collect();

        var active = ReadDedicatedUsage().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                var gpu = new Gpu { Name = description, LuidKey = luidKey, TotalGb = memory / 1073741824f };
                var open = new OpenAdapterFromLuid { LowPart = (uint)luid, HighPart = (int)(luid >> 32) };
                if (D3DKMTOpenAdapterFromLuid(ref open) == 0) gpu.Handle = open.Adapter;
                gpus.Add(gpu);
            }
        }
        _gpus = [.. gpus.OrderByDescending(g => g.TotalGb).Take(GpuCount)];
        foreach (var (name, limit) in Nvml.ReadPowerLimits())
        {
            if (_gpus.FirstOrDefault(g => g.Name == name) is { } match) match.PowerLimitW = limit;
        }
        foreach (var gpu in gpus.Except(_gpus)) Close(gpu);
    }

    public HardwareSample Sample()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        float totalGb = memory.TotalPhysical / 1073741824f;
        float usedGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824f;

        _pdh.Collect();
        var engineLoad = ReadEngineLoad();
        var dedicated = ReadDedicatedUsage();
        var driver = _driver.Update();

        var gpus = new List<GpuSample>(_gpus.Length);
        foreach (var gpu in _gpus)
        {
            float? load = null;
            var engines = new Dictionary<string, EngineLoad>();
            foreach (var ((luid, index, type), value) in engineLoad)
            {
                if (!luid.Equals(gpu.LuidKey, StringComparison.OrdinalIgnoreCase)) continue;
                float clamped = (float)Math.Min(value, 100);
                load = Math.Max(load ?? 0, clamped);

                // Engines sharing a type (several "Copy" nodes) are shown as one, at the busiest value.
                engines[type] = engines.TryGetValue(type, out var existing)
                    ? existing with { Load = Math.Max(existing.Load, clamped) }
                    : new EngineLoad(type, int.Parse(index), clamped);
            }
            driver.TryGetValue(gpu.Name, out var reading);
            var (temperature, powerShare) = ReadPerformance(gpu);
            gpus.Add(new GpuSample(
                gpu.Name, load, [.. engines.Values.OrderBy(e => e.Index)], temperature, reading.ClockMhz,
                reading.PowerW ?? gpu.PowerLimitW * powerShare,
                dedicated.TryGetValue(gpu.LuidKey, out double bytes) ? (float)(bytes / 1073741824) : null,
                gpu.TotalGb));
        }

        return new HardwareSample(
            (float)Math.Min(_pdh.Value(_cpu) ?? 0, 100),
            (float)((_pdh.Value(_cpuBaseMhz) ?? 0) * (_pdh.Value(_cpuPerformance) ?? 0) / 100000),
            _driver.CpuTemp, _driver.CpuPower,
            gpus, memory.MemoryLoad, usedGb, totalGb);
    }

    /// <summary>Utilisation per (adapter, engine index, engine type), summed over processes.</summary>
    Dictionary<(string Luid, string Index, string Type), double> ReadEngineLoad()
    {
        var result = new Dictionary<(string, string, string), double>();
        foreach (var (instance, value) in _pdh.Values(_engines))
        {
            var match = EngineInstance().Match(instance);
            if (!match.Success) continue;

            var key = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            result[key] = result.GetValueOrDefault(key) + value;
        }
        return result;
    }

    /// <summary>Dedicated memory in use, in bytes, by adapter LUID.</summary>
    Dictionary<string, double> ReadDedicatedUsage()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in _pdh.Values(_adapterMemory))
            result[LuidKey(instance)] = value;
        return result;
    }

    /// <summary>Temperature in °C and power draw as a fraction of the adapter's limit.</summary>
    static (float? Temperature, float? PowerShare) ReadPerformance(Gpu gpu)
    {
        if (gpu.Handle == 0) return default;

        int size = Marshal.SizeOf<AdapterPerfData>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new AdapterPerfData(), buffer, false);
            var query = new QueryAdapterInfo
            {
                Adapter = gpu.Handle,
                Type = AdapterPerfDataType,
                Data = buffer,
                DataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return default;

            // Temperature is in deci-degrees and power in tenths of a percent; 0 means not reported.
            var data = Marshal.PtrToStructure<AdapterPerfData>(buffer);
            return (
                data.Temperature > 0 ? data.Temperature / 10f : null,
                data.Power > 0 ? data.Power / 1000f : null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
        _pdh.Dispose();
        _driver.Dispose();
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
