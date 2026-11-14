using System.Runtime.InteropServices;

namespace ScreenUpdater;

/// <summary>Tracks whether the console display is on, via GUID_CONSOLE_DISPLAY_STATE.</summary>
public sealed class DisplayPower : IDisposable
{
    const int DeviceNotifyCallback = 2;
    const uint PbtPowerSettingChange = 0x8013;

    delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct SubscribeParameters
    {
        public NotifyCallback Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerSettingRegisterNotification(
        ref Guid settingGuid, int flags, ref SubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    static extern uint PowerSettingUnregisterNotification(IntPtr registration);

    readonly NotifyCallback _callback;
    readonly IntPtr _registration;
    volatile bool _on = true;

    public bool IsOn => _on;

    public DisplayPower()
    {
        _callback = OnNotify;
        var guid = new Guid(0x6fe69556, 0x704a, 0x47a0, 0x8f, 0x24, 0xc2, 0x8d, 0x93, 0x6f, 0xda, 0x47);
        var parameters = new SubscribeParameters { Callback = _callback };
        PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, ref parameters, out _registration);
    }

    uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PbtPowerSettingChange && setting != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: Guid (16) + DataLength (4) + Data; 0 = off, 1 = on, 2 = dimmed
            _on = Marshal.ReadInt32(setting, 20) != 0;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
            PowerSettingUnregisterNotification(_registration);
    }
}
