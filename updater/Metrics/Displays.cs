using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

public static class Displays
{
    const int AttachedToDesktop = 1;
    const int CurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice display, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode settings);

    /// <summary>Distinct refresh rates in Hz of the displays on the desktop, highest first.</summary>
    public static int[] RefreshRates()
    {
        var rates = new SortedSet<int>();
        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref display, 0); i++)
        {
            if ((display.StateFlags & AttachedToDesktop) == 0) continue;

            var settings = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(display.DeviceName, CurrentSettings, ref settings) && settings.DisplayFrequency > 1)
                rates.Add((int)settings.DisplayFrequency);
        }
        return [.. rates.Reverse()];
    }
}
