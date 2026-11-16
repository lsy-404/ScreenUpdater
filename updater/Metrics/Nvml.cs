using System.Runtime.InteropServices;
using System.Text;

namespace ScreenUpdater.Metrics;

/// <summary>Power limit and utilisation of NVIDIA adapters, from the management library shipped with the driver.</summary>
public sealed class Nvml : IDisposable
{
    const string Dll = "nvml.dll";

    [DllImport(Dll)]
    static extern int nvmlInit_v2();

    [DllImport(Dll)]
    static extern int nvmlShutdown();

    [DllImport(Dll)]
    static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport(Dll)]
    static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(Dll)]
    static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport(Dll)]
    static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);

    [StructLayout(LayoutKind.Sequential)]
    struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport(Dll)]
    static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);

    readonly Dictionary<string, IntPtr> _devices = [];
    readonly bool _initialised;

    /// <summary>Finds no devices without an NVIDIA driver.</summary>
    public Nvml()
    {
        try
        {
            if (nvmlInit_v2() != 0) return;
        }
        catch (DllNotFoundException)
        {
            return;
        }
        _initialised = true;

        if (nvmlDeviceGetCount_v2(out uint count) != 0) return;
        var buffer = new byte[96];
        for (uint i = 0; i < count; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0) continue;
            if (nvmlDeviceGetName(device, buffer, (uint)buffer.Length) != 0) continue;
            _devices[Encoding.ASCII.GetString(buffer, 0, Math.Max(Array.IndexOf(buffer, (byte)0), 0))] = device;
        }
    }

    /// <summary>Power limit in watts of the adapter with this name.</summary>
    public float? PowerLimit(string adapterName) =>
        _devices.TryGetValue(adapterName, out var device) && nvmlDeviceGetPowerManagementLimit(device, out uint milliwatts) == 0
            ? milliwatts / 1000f
            : null;

    /// <summary>
    /// Utilisation in percent as the driver measures it, which includes compute work
    /// that never shows up in the Windows engine counters.
    /// </summary>
    public float? Load(string adapterName) =>
        _devices.TryGetValue(adapterName, out var device) && nvmlDeviceGetUtilizationRates(device, out var utilization) == 0
            ? utilization.Gpu
            : null;

    public void Dispose()
    {
        if (_initialised) nvmlShutdown();
    }
}
