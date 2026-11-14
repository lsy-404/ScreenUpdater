using System.Runtime.InteropServices;
using System.Text;

namespace ScreenUpdater.Metrics;

/// <summary>Power limits of NVIDIA adapters, from the management library shipped with the driver.</summary>
public static class Nvml
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

    /// <summary>Power limit in watts by adapter name; empty without an NVIDIA driver.</summary>
    public static Dictionary<string, float> ReadPowerLimits()
    {
        var limits = new Dictionary<string, float>();
        try
        {
            if (nvmlInit_v2() != 0) return limits;
        }
        catch (DllNotFoundException)
        {
            return limits;
        }

        if (nvmlDeviceGetCount_v2(out uint count) == 0)
        {
            var buffer = new byte[96];
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0) continue;
                if (nvmlDeviceGetName(device, buffer, (uint)buffer.Length) != 0) continue;
                if (nvmlDeviceGetPowerManagementLimit(device, out uint milliwatts) != 0) continue;

                var name = Encoding.ASCII.GetString(buffer, 0, Math.Max(Array.IndexOf(buffer, (byte)0), 0));
                limits[name] = milliwatts / 1000f;
            }
        }

        nvmlShutdown();
        return limits;
    }
}
