using LibreHardwareMonitor.Hardware;

namespace ScreenUpdater.Metrics;

public sealed record GpuSample(string Name, float? Load, float? Temp, float? Power, float? VramUsedMb);

public sealed record HardwareSample(
    float? CpuLoad, float? CpuTemp, float? CpuPower,
    IReadOnlyList<GpuSample> Gpus,
    float? MemLoad, float? MemUsedGb, float? MemTotalGb);

public sealed class Sensors : IDisposable
{
    public const int GpuCount = 2;

    readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
    };

    readonly IHardware[] _gpus;

    public Sensors()
    {
        _computer.Open();

        // The adapters with the most dedicated memory, which leaves out integrated graphics.
        _gpus = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
            .Select(h =>
            {
                h.Update();
                return (Hardware: h, Memory: Value(h, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total") ?? 0);
            })
            .OrderByDescending(g => g.Memory)
            .Take(GpuCount)
            .Select(g => g.Hardware)
            .ToArray();
    }

    public HardwareSample Sample()
    {
        float? cpuLoad = null, cpuTemp = null, cpuPower = null;
        float? memLoad = null, memUsed = null, memAvailable = null;

        foreach (var hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    hardware.Update();
                    cpuLoad = Value(hardware, SensorType.Load, "CPU Total");
                    cpuTemp = Value(hardware, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core Max");
                    cpuPower = Value(hardware, SensorType.Power, "Package", "CPU Package");
                    break;
                case HardwareType.Memory when !hardware.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase):
                    hardware.Update();
                    memLoad = Value(hardware, SensorType.Load, "Memory");
                    memUsed = Value(hardware, SensorType.Data, "Memory Used");
                    memAvailable = Value(hardware, SensorType.Data, "Memory Available");
                    break;
            }
        }

        var gpus = new List<GpuSample>(_gpus.Length);
        foreach (var gpu in _gpus)
        {
            gpu.Update();
            gpus.Add(new GpuSample(
                gpu.Name,
                Value(gpu, SensorType.Load, "GPU Core", "D3D 3D"),
                Value(gpu, SensorType.Temperature, "GPU Core"),
                Value(gpu, SensorType.Power, "GPU Package", "GPU Core"),
                Value(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used")));
        }

        return new HardwareSample(cpuLoad, cpuTemp, cpuPower, gpus, memLoad, memUsed, memUsed + memAvailable);
    }

    static float? Value(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != type || sensor.Name != name || sensor.Value is not { } value) continue;
                // Temperature and power read 0 when the driver cannot reach the sensor.
                if (value == 0 && type is SensorType.Temperature or SensorType.Power) continue;
                return value;
            }
        }
        return null;
    }

    public void Dispose() => _computer.Close();
}
