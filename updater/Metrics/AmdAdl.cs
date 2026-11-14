using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenUpdater.Metrics;

/// <summary>Radeon core clock and board power from the AMD Display Library of the installed driver.</summary>
public sealed class AmdAdl : IDisposable
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    const string Library = "atiadlxx.dll";
    const int MaxPath = 256;
    const int MaxSensors = 256;

    // ADL_PMLOG_SENSORS
    const int SensorGfxClock = 1;
    const int SensorAsicPower = 23;
    const int SensorGfxPower = 30;
    const int SensorBoardPower = 73;

    // AdapterInfo layout
    const int AdapterInfoSize = 8 + MaxPath + 16 + MaxPath * 2 + 8 + MaxPath * 3 + 4;
    const int AdapterIndexOffset = 4;
    const int AdapterNameOffset = 8 + MaxPath + 16;

    delegate IntPtr AllocCallback(int size);
    delegate int MainControlCreate(AllocCallback callback, int connectedOnly, out IntPtr context);
    delegate int MainControlDestroy(IntPtr context);
    delegate int NumberOfAdaptersGet(IntPtr context, out int count);
    delegate int AdapterInfoGet(IntPtr context, IntPtr info, int size);
    delegate int QueryPmLogData(IntPtr context, int adapterIndex, IntPtr output);

    static readonly AllocCallback Alloc = Marshal.AllocHGlobal;

    readonly IntPtr _library;
    readonly IntPtr _context;
    readonly MainControlDestroy? _destroy;
    readonly QueryPmLogData? _query;
    readonly List<(string Name, int Index)> _adapters = [];
    readonly IntPtr _buffer = Marshal.AllocHGlobal(4 + MaxSensors * 8);

    public AmdAdl()
    {
        if (LocateLibrary() is not { } path || !NativeLibrary.TryLoad(path, out _library)) return;

        var create = Export<MainControlCreate>("ADL2_Main_Control_Create");
        var count = Export<NumberOfAdaptersGet>("ADL2_Adapter_NumberOfAdapters_Get");
        var info = Export<AdapterInfoGet>("ADL2_Adapter_AdapterInfo_Get");
        _destroy = Export<MainControlDestroy>("ADL2_Main_Control_Destroy");
        _query = Export<QueryPmLogData>("ADL2_New_QueryPMLogData_Get");
        if (create is null || count is null || info is null || _query is null) return;

        if (create(Alloc, 1, out _context) != 0) return;
        if (count(_context, out int adapters) != 0 || adapters <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(AdapterInfoSize * adapters);
        try
        {
            if (info(_context, buffer, AdapterInfoSize * adapters) != 0) return;
            for (int i = 0; i < adapters; i++)
            {
                IntPtr entry = buffer + i * AdapterInfoSize;
                var name = Marshal.PtrToStringAnsi(entry + AdapterNameOffset) ?? "";
                int index = Marshal.ReadInt32(entry, AdapterIndexOffset);

                // One GPU appears once per display output; the first index that reports sensors is enough.
                if (_adapters.Any(a => a.Name == name) || Query(index) is null) continue;
                _adapters.Add((name, index));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The copy next to the active display driver. The one in System32 can belong to a different
    /// driver package, and then every query reports "not supported".
    /// </summary>
    static string? LocateLibrary()
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(DisplayClass);
        foreach (var name in adapters?.GetSubKeyNames() ?? [])
        {
            if (name.Length != 4 || !name.All(char.IsDigit)) continue;
            using var adapter = adapters!.OpenSubKey(name);
            var driver = adapter?.GetValue("OpenGLDriverName") switch
            {
                string value => value,
                string[] { Length: > 0 } values => values[0],
                _ => null,
            };
            if (Path.GetDirectoryName(driver) is not { } directory) continue;

            var candidate = Path.Combine(directory, Library);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    T? Export<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    /// <summary>Adds a reading per Radeon adapter, keyed by adapter name.</summary>
    public void Update(Dictionary<string, GpuDriverReading> readings)
    {
        foreach (var (name, index) in _adapters)
        {
            if (Query(index) is { } reading) readings[name] = reading;
        }
    }

    GpuDriverReading? Query(int adapterIndex)
    {
        if (_query!(_context, adapterIndex, _buffer) != 0) return null;

        // ADLPMLogDataOutput: size, then (supported, value) per sensor.
        float? Sensor(int id) =>
            Marshal.ReadInt32(_buffer, 4 + id * 8) != 0 ? Marshal.ReadInt32(_buffer, 8 + id * 8) : null;

        var clock = Sensor(SensorGfxClock);
        var power = Sensor(SensorBoardPower) ?? Sensor(SensorAsicPower) ?? Sensor(SensorGfxPower);
        return clock is null && power is null ? null : new GpuDriverReading(clock, power);
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero) _destroy?.Invoke(_context);
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        Marshal.FreeHGlobal(_buffer);
    }
}
