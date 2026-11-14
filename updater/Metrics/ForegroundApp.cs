using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

public readonly record struct ForegroundInfo(int ProcessId, string Name);

public static class ForegroundApp
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static ForegroundInfo? Get()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new ForegroundInfo((int)pid, process.ProcessName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
