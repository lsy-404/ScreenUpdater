using LibreHardwareMonitor.Hardware;

namespace ScreenUpdater.Metrics;

public sealed record HardwareSample(
    float? CpuLoad, float? CpuTemp, float? CpuPower,
    string? GpuName, float? GpuLoad, float? GpuTemp, float? GpuPower, float? VramUsedMb, float? VramTotalMb,
    float? MemLoad, float? MemUsedGb, float? MemTotalGb);

public sealed class Sensors : IDisposable
{
    readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
    };

    public Sensors() => _computer.Open();

    public HardwareSample Sample()
    {
        float? cpuLoad = null, cpuTemp = null, cpuPower = null;
        float? memLoad = null, memUsed = null, memAvailable = null;
        IHardware? gpu = null;
        float gpuBest = -1;

        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuLoad = Value(hardware, SensorType.Load, "CPU Total");
                    cpuTemp = Value(hardware, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core Max");
                    cpuPower = Value(hardware, SensorType.Power, "Package", "CPU Package");
                    break;
                case HardwareType.Memory when hardware.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase):
                    break;
                case HardwareType.Memory:
                    memLoad = Value(hardware, SensorType.Load, "Memory");
                    memUsed = Value(hardware, SensorType.Data, "Memory Used");
                    memAvailable = Value(hardware, SensorType.Data, "Memory Available");
                    break;
                case HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel:
                    float load = GpuLoad(hardware) ?? 0;
                    if (load > gpuBest)
                    {
                        gpuBest = load;
                        gpu = hardware;
                    }
                    break;
            }
        }

        return new HardwareSample(
            cpuLoad, cpuTemp, cpuPower,
            gpu?.Name,
            gpu is null ? null : GpuLoad(gpu),
            gpu is null ? null : Value(gpu, SensorType.Temperature, "GPU Core"),
            gpu is null ? null : Value(gpu, SensorType.Power, "GPU Package", "GPU Core"),
            gpu is null ? null : Value(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            gpu is null ? null : Value(gpu, SensorType.SmallData, "GPU Memory Total"),
            memLoad, memUsed, memUsed + memAvailable);
    }

    static float? GpuLoad(IHardware gpu) => Value(gpu, SensorType.Load, "GPU Core", "D3D 3D");

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
