using System.Runtime.InteropServices;

namespace ScreenUpdater.Metrics;

/// <summary>Performance counters through the PDH API, the interface Task Manager and perfmon read.</summary>
public sealed class PdhQuery : IDisposable
{
    const uint FormatDouble = 0x200;
    const uint FormatNoCap100 = 0x8000;
    const uint MoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    readonly IntPtr _query;

    public PdhQuery()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X8}");
    }

    /// <param name="path">English counter path; an instance of <c>*</c> selects every instance.</param>
    public IntPtr Add(string path)
    {
        uint status = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter);
        if (status != 0) throw new InvalidOperationException($"counter {path} unavailable: 0x{status:X8}");
        return counter;
    }

    public void Collect() => PdhCollectQueryData(_query);

    public double? Value(IntPtr counter) =>
        PdhGetFormattedCounterValue(counter, FormatDouble | FormatNoCap100, IntPtr.Zero, out var value) == 0 && value.Status <= 1
            ? value.Value
            : null;

    /// <summary>Values of a wildcard counter by instance name; instances sharing a name are summed.</summary>
    public Dictionary<string, double> Values(IntPtr counter)
    {
        var result = new Dictionary<string, double>();
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero) != MoreData)
            return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | FormatNoCap100, ref size, out uint count, buffer) != 0)
                return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer (8), status (4), padding (4), double (8).
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * 24;
                if (Marshal.ReadInt32(item, 8) > 1) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                result[name] = result.GetValueOrDefault(name) + value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);
}
