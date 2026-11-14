using LibreHardwareMonitor.Hardware;

namespace ScreenUpdater.Metrics;

public readonly record struct GpuDriverReading(float? ClockMhz, float? PowerW);

/// <summary>
/// Readings that need vendor or kernel-driver access: CPU temperature and package power (via PawnIO),
/// GPU core clock and power draw.
/// </summary>
public sealed class DriverSensors : IDisposable
{
    readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };

    public float? CpuTemp { get; private set; }
    public float? CpuPower { get; private set; }

    public DriverSensors() => _computer.Open();

    /// <summary>Refreshes the CPU readings and returns GPU readings keyed by adapter name.</summary>
    public Dictionary<string, GpuDriverReading> Update()
    {
        var gpus = new Dictionary<string, GpuDriverReading>();
        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                CpuTemp = Value(hardware, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package");
                CpuPower = Value(hardware, SensorType.Power, "Package", "CPU Package");
            }
            else
            {
                gpus[hardware.Name] = new GpuDriverReading(
                    Value(hardware, SensorType.Clock, "GPU Core"),
                    Value(hardware, SensorType.Power, "GPU Package", "GPU Core"));
            }
        }
        return gpus;
    }

    static float? Value(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var sensor in hardware.Sensors)
            {
                // A reading of 0 means the driver could not reach the sensor.
                if (sensor.SensorType == type && sensor.Name == name && sensor.Value is > 0 and var value)
                    return value;
            }
        }
        return null;
    }

    public void Dispose() => _computer.Close();
}
