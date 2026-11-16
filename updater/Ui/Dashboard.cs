using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ScreenUpdater.Lcd;
using ScreenUpdater.Metrics;

namespace ScreenUpdater.Ui;

public sealed class History(int capacity)
{
    readonly float[] _values = Enumerable.Repeat(float.NaN, capacity).ToArray();
    int _next;

    public int Capacity => capacity;

    public void Add(float? value)
    {
        _values[_next] = value ?? float.NaN;
        _next = (_next + 1) % capacity;
    }

    /// <summary>Oldest first.</summary>
    public float this[int index] => _values[(_next + index) % capacity];

    public float Max()
    {
        float max = 0;
        foreach (var v in _values)
            if (v > max) max = v;
        return max;
    }
}

/// <summary>One engine type of an adapter, e.g. "3D" or "VideoEncode".</summary>
public sealed class EngineTrack(int index)
{
    public int Index => index;
    public History History { get; } = new(DashboardState.HistoryLength);
}

public sealed class GpuState
{
    public readonly History Load = new(DashboardState.HistoryLength);

    /// <summary>Dedicated memory in use, as a percentage of the adapter's total.</summary>
    public readonly History Memory = new(DashboardState.HistoryLength);
    public readonly Dictionary<int, EngineTrack> Engines = [];

    public void Add(GpuSample sample)
    {
        Load.Add(sample.Load);
        Memory.Add(sample.VramUsedGb / sample.VramTotalGb * 100);
        foreach (var engine in sample.Engines)
        {
            if (!Engines.TryGetValue(engine.Index, out var track))
                Engines[engine.Index] = track = new EngineTrack(engine.Index);
            track.History.Add(engine.Load);
        }
    }
}

public sealed class DashboardState
{
    public const int HistoryLength = 120;

    public bool FramesAvailable;

    /// <summary>The foreground application has presented before, so no frames means it is idle.</summary>
    public bool FramesSupported;
    public FrameStats? Frames;

    /// <summary>Refresh rates of the connected displays, highest first.</summary>
    public int[] RefreshRates = [];
    public HardwareSample? Hardware;
    public SystemSample? System;
    public WeatherReport? Weather;

    public readonly History Fps = new(HistoryLength);
    public readonly History Cpu = new(HistoryLength);
    public readonly History Mem = new(HistoryLength);
    public readonly History Integrated = new(HistoryLength);
    public readonly History Download = new(HistoryLength);
    public readonly History Upload = new(HistoryLength);
    public readonly GpuState[] Gpus = [.. Enumerable.Range(0, Sensors.GpuCount).Select(_ => new GpuState())];
}

public sealed class Dashboard : IDisposable
{
    static readonly Color Background = Color.FromArgb(0x1c, 0x1c, 0x1c);
    static readonly Color Card = Color.FromArgb(0x2b, 0x2b, 0x2b);
    static readonly Color Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
    static readonly Color Grid = Color.FromArgb(0x45, 0x45, 0x45);
    static readonly Color Text = Color.White;
    static readonly Color Muted = Color.FromArgb(0xa0, 0xa0, 0xa0);
    static readonly Color FpsColor = Color.FromArgb(0xfc, 0xe1, 0x00);
    static readonly Color CpuColor = Color.FromArgb(0x4c, 0xc2, 0xff);
    static readonly Color MemColor = Color.FromArgb(0xb4, 0xa7, 0xf5);
    static readonly Color DownColor = Color.FromArgb(0x6c, 0xcb, 0x5f);
    static readonly Color UpColor = Color.FromArgb(0xff, 0x99, 0xa4);
    static readonly Color DiskColor = Color.FromArgb(0xf0, 0xa5, 0x60);
    static readonly Color GpuColor =Color.FromArgb(0x5f, 0xd0, 0xb8);
    static readonly Color AmdColor = Color.FromArgb(0xff, 0x5c, 0x5c);
    static readonly Color NvidiaColor = Color.FromArgb(0x76, 0xb9, 0x00);
    static readonly Color IntelColor = Color.FromArgb(0x00, 0x9f, 0xe3);

    static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    const int Width = TuringLcd.Width;
    const int Height = TuringLcd.Height;
    const int Pad = 6;
    const int TopY = 26;
    const int TopHeight = 112;
    const int LeftWidth = 124;
    const int FpsHeight = 68;
    const int NetworkWidth = 92;
    const int EngineCharts = 4;

    readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    readonly Graphics _g;

    readonly Font _title = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _label = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _value = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _small = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _smallBold = new("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _number = new("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _tiny = new("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);

    readonly SolidBrush _text = new(Text);
    readonly SolidBrush _muted = new(Muted);

    readonly StringFormat _left = new(StringFormat.GenericTypographic)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };
    readonly StringFormat _center = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };
    readonly StringFormat _right = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Far,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    public Dashboard()
    {
        _g = Graphics.FromImage(_bitmap);
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    /// <param name="shift">Offset of the whole layout, moved over time so static pixels do not persist.</param>
    public Bitmap Render(DashboardState state, Point shift)
    {
        _g.SmoothingMode = SmoothingMode.None;
        _g.ResetTransform();
        _g.Clear(Background);
        _g.TranslateTransform(shift.X, shift.Y);

        DrawHeader(state);

        int bottom = TopY + TopHeight;
        int right = Width - Pad;
        DrawFpsCard(new Rectangle(Pad, TopY, LeftWidth, FpsHeight), state);
        DrawCountsCard(Rectangle.FromLTRB(Pad, TopY + FpsHeight + Pad, Pad + LeftWidth, bottom), state.System);
        DrawSystemCard(Rectangle.FromLTRB(Pad + LeftWidth + Pad, TopY, right - NetworkWidth - Pad, bottom), state);
        DrawNetworkCard(Rectangle.FromLTRB(right - NetworkWidth, TopY, right, bottom), state);

        int y = bottom + Pad;
        int w = (right - Pad - (Sensors.GpuCount - 1) * Pad) / Sensors.GpuCount;
        for (int i = 0; i < Sensors.GpuCount; i++)
        {
            var gpu = state.Hardware is { } hw && i < hw.Gpus.Count ? hw.Gpus[i] : null;
            DrawGpuCard(new Rectangle(Pad + i * (w + Pad), y, w, Height - y - Pad), gpu, state.Gpus[i]);
        }

        return _bitmap;
    }

    void DrawHeader(DashboardState state)
    {
        var now = DateTime.Now;
        var clock = $"{now.Month}月{now.Day}日 {Weekdays[(int)now.DayOfWeek]} {now:HH:mm:ss}";
        _g.DrawString(clock, _title, _text, Pad + 2, 4, _left);

        // Space used on the fixed drives as a bar, then read and write throughput.
        var system = state.System;
        float x = Pad + 2 + _g.MeasureString("00月00日 周六 00:00:00", _title, 300, _left).Width + 8;
        const int y = 6;
        using var accent = new SolidBrush(DiskColor);
        x = DrawInline(x, y, ("SSD", "", "SSD", _smallBold, accent));

        var bar = new Rectangle((int)x, y + 3, 28, 7);
        using (var track = new SolidBrush(Grid))
            _g.FillRectangle(track, bar);
        float? share = system is { DiskTotalGb: > 0 } ? system.DiskUsedGb / system.DiskTotalGb : null;
        if (share is { } used)
            _g.FillRectangle(accent, bar with { Width = (int)(bar.Width * used) });

        var read = Rate(system?.DiskReadBps);
        var write = Rate(system?.DiskWriteBps);
        x = DrawInline(bar.Right + 2, y, (Number(share * 100), "%", "100", _number, _muted));
        x = DrawRate(x + 5, y, "R", read);
        x = DrawRate(x + 9, y, "W", write);

        if (state.Weather is { } weather)
        {
            var text = $"{weather.Condition} {weather.Temperature:0}°C · {weather.Low:0}~{weather.High:0}°C";
            _g.DrawString(text, _label, _muted, RectangleF.FromLTRB(x + 4, 5, Width - Pad - 2, 23), _right);
        }
    }

    /// <summary>A label, a value of constant width, and the unit centred in a slot of its own.</summary>
    float DrawRate(float x, int y, string label, (string Value, string Unit) rate)
    {
        const int unitGap = 3;
        const int unitSlot = 9;
        x = DrawInline(x, y, (label, "", "W", _small, _muted));
        float width = _g.MeasureString("000.00", _number, 300, _left).Width;
        _g.DrawString(rate.Value, _number, _text, new RectangleF(x - 20, y - 1, width + 20, 14), _right);
        if (rate.Value.Length > 0)
            _g.DrawString(rate.Unit, _tiny, _muted, new RectangleF(x + width + unitGap, y + 1, unitSlot, 12), _center);
        return x + width + unitGap + unitSlot;
    }

    /// <summary>Frame rate of the foreground application: history on top, percentiles underneath.</summary>
    void DrawFpsCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        int x = rect.X + 8;
        int y = rect.Y + 4;
        int w = rect.Width - 16;

        _g.DrawString("FPS", _small, _muted, x, y, _left);

        var frames = state.Frames;
        if (frames is { } current)
            _g.DrawString(current.Fps.ToString("0"), _number, _text, new RectangleF(x, y - 1, w, 14), _right);

        // The axis ends at the fastest display; every display's refresh rate gets a line and a label beside the chart.
        const int gutter = 20;
        const int labelHeight = 9;
        var area = new Rectangle(x, y + 14, w, rect.Height - 38);
        var chart = area with { Width = w - gutter };
        var rates = state.RefreshRates.Length > 0 ? state.RefreshRates : [60];
        float max = rates[0];
        using (var pen = new Pen(Grid))
        {
            _g.DrawLine(pen, chart.X, chart.Bottom, chart.Right, chart.Bottom);
            for (int i = 0; i < rates.Length && (i + 1) * labelHeight <= chart.Height + 3; i++)
            {
                int line = chart.Bottom - (int)(chart.Height * rates[i] / max);
                _g.DrawLine(pen, chart.X, line, chart.Right, line);
                _g.DrawString(rates[i].ToString(), _tiny, i == 0 ? _text : _muted,
                    new RectangleF(chart.Right, chart.Y - 4 + i * labelHeight, gutter, 12), _right);
            }
        }
        DrawSeries(chart, state.Fps, max, FpsColor, fill: true);

        if (!state.FramesAvailable) DrawNotice(area, "FPS 指标离线");
        else if (frames is null && !state.FramesSupported) DrawNotice(area, "当前窗口不支持 FPS");

        var rows = new (string Name, double? Ms)[]
        {
            ("P50", frames?.P50Ms), ("P95", frames?.P95Ms), ("P99", frames?.P99Ms),
        };
        int third = w / rows.Length;
        for (int i = 0; i < rows.Length; i++)
        {
            int cx = x + i * third;
            _g.DrawString(rows[i].Name, _tiny, _muted, cx, chart.Bottom + 5, _left);
            _g.DrawString(rows[i].Ms is { } ms ? (1000 / ms).ToString("0") : "—", _smallBold, _text, cx + 16, chart.Bottom + 4, _left);
        }
    }

    /// <summary>A boxed message over the middle of a chart.</summary>
    void DrawNotice(Rectangle chart, string message)
    {
        int width = (int)MathF.Ceiling(_g.MeasureString(message, _small, 300, _left).Width) + 8;
        var box = new Rectangle(chart.X + (chart.Width - width) / 2, chart.Y + (chart.Height - 16) / 2, width, 16);
        using var fill = new SolidBrush(Background);
        using var border = new Pen(Border);
        _g.FillRectangle(fill, box);
        _g.DrawRectangle(border, box);
        _g.DrawString(message, _small, _text, box.X + 4, box.Y + 2, _left);
    }

    void DrawCountsCard(Rectangle rect, SystemSample? system)
    {
        DrawCard(rect);
        var columns = new (string Name, int? Value)[]
        {
            ("进程", system?.Processes), ("线程", system?.Threads), ("句柄", system?.Handles),
        };
        int x = rect.X + 8;
        int w = (rect.Width - 16) / columns.Length;
        for (int i = 0; i < columns.Length; i++)
        {
            _g.DrawString(columns[i].Name, _small, _muted, x + i * w, rect.Y + 5, _left);
            _g.DrawString(columns[i].Value?.ToString() ?? "—", _smallBold, _text, x + i * w, rect.Y + 19, _left);
        }
    }

    void DrawSystemCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        var hw = state.Hardware;
        int x = rect.X + 8;
        int y = rect.Y + 4;

        using var cpu = new SolidBrush(CpuColor);
        using var memory = new SolidBrush(MemColor);
        float end = DrawInline(x, y,
            ("CPU", "", "CPU", _smallBold, cpu),
            (Number(hw?.CpuLoad), "%", "100", _number, _muted),
            (Number(hw?.CpuGhz, "0.00"), "GHz", "0.00", _number, _muted),
            (Number(hw?.CpuPower), "W", "000", _number, _muted),
            (Number(hw?.CpuTemp), "°C", "00", _number, _muted));
        DrawInline(end + 2, y,
            ("内存", "", "内存", _smallBold, memory),
            (Number(hw?.MemLoad), "%", "100", _number, _muted),
            (Number(hw?.MemUsedGb, "0.0"), "G", "00.0", _number, _muted));
        var chart = Rectangle.FromLTRB(x, y + 17, rect.Right - 8, rect.Bottom - 10);
        DrawAxes(chart, hw?.MemTotalGb, Unit(state.System?.MemoryMhz, "MHz"));
        if (hw?.IntegratedLoad is not null)
        {
            using var integrated = new SolidBrush(GpuColor);
            _g.DrawString("核显", _tiny, integrated, chart.X + 30, chart.Y + 2, _left);
        }
        DrawSeries(chart, state.Mem, 100, MemColor);
        DrawSeries(chart, state.Integrated, 100, GpuColor);
        DrawSeries(chart, state.Cpu, 100, CpuColor, fill: true);
        DrawTimeAxis(chart);
    }

    /// <summary>Upload rises from the centre line and download hangs below it, on a shared scale.</summary>
    void DrawNetworkCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        int x = rect.X + 8;
        int w = rect.Width - 16;

        DrawSpeed(x, rect.Y + 4, w, "↑", UpColor, state.System?.UploadBps);
        DrawSpeed(x, rect.Y + 19, w, "↓", DownColor, state.System?.DownloadBps);

        var chart = Rectangle.FromLTRB(x, rect.Y + 38, rect.Right - 8, rect.Bottom - 6);
        var upper = chart with { Height = chart.Height / 2 };
        var lower = Rectangle.FromLTRB(chart.X, upper.Bottom, chart.Right, chart.Bottom);
        using (var pen = new Pen(Grid))
            _g.DrawLine(pen, chart.X, upper.Bottom, chart.Right, upper.Bottom);

        float max = MathF.Max(MathF.Max(state.Download.Max(), state.Upload.Max()), 16384);
        DrawSeries(upper, state.Upload, max, UpColor, fill: true, compress: true);
        DrawSeries(lower, state.Download, max, DownColor, fill: true, downward: true, compress: true);
    }
    void DrawSpeed(int x, int y, int width, string arrow, Color color, double? bytesPerSecond)
    {
        using (var brush = new SolidBrush(color))
            _g.DrawString(arrow, _value, brush, x, y - 1, _left);
        if (bytesPerSecond is not { } bps) return;

        var (value, unit) = Speed(bps);
        _g.DrawString(unit, _small, _muted, new RectangleF(x, y + 2, width, 12), _right);
        _g.DrawString(value, _value, _text, new RectangleF(x, y, width - 25, 16), _right);
    }

    void DrawGpuCard(Rectangle rect, GpuSample? gpu, GpuState state)
    {
        DrawCard(rect);
        int x = rect.X + 10;
        int y = rect.Y + 4;
        int w = rect.Width - 20;
        var color = GpuColor;

        using var name = new SolidBrush(VendorColor(gpu?.Name));
        using var memory = new SolidBrush(MemColor);
        var title = ShortGpuName(gpu?.Name);
        DrawInline(x, y,
            (title, "", title, _smallBold, name),
            (Number(gpu?.Load), "%", "100", _number, _muted),
            (Number(gpu?.Temp), "°C", "00", _number, _muted),
            (Number(gpu?.VramUsedGb, "0.0"), "G", "00.0", _number, memory),
            (Number(gpu?.PowerW), "W", "000", _number, _muted),
            (Number(gpu?.ClockMhz / 1000, "0.00"), "GHz", "0.00", _number, _muted));
        var chart = new Rectangle(x, y + 17, w, 62);
        DrawAxes(chart, gpu?.VramTotalGb, "");
        DrawSeries(chart, state.Memory, 100, MemColor);
        DrawSeries(chart, state.Load, 100, color, fill: true);
        DrawTimeAxis(chart);

        var engines = state.Engines.OrderBy(e => e.Value.Index).Take(EngineCharts).ToList();

        const int gap = 6;
        int top = chart.Bottom + 9;
        int cellW = (w - gap) / 2;
        int cellH = (rect.Bottom - 6 - top - 3) / 2;
        for (int i = 0; i < engines.Count; i++)
        {
            var (index, track) = engines[i];
            var cell = new Rectangle(x + i % 2 * (cellW + gap), top + i / 2 * (cellH + 3), cellW, cellH);
            var engine = gpu?.Engines.FirstOrDefault(e => e.Index == index);
            float? load = engine?.Load;
            _g.DrawString(engine?.Type ?? "", _small, _muted, new RectangleF(cell.X, cell.Y, cell.Width - 26, 12), _left);
            _g.DrawString(Percent(load), _smallBold, _text, new RectangleF(cell.X, cell.Y, cell.Width, 12), _right);

            var engineChart = Rectangle.FromLTRB(cell.X, cell.Y + 13, cell.Right, cell.Bottom);
            DrawGrid(engineChart);
            DrawSeries(engineChart, track.History, 100, color, fill: true);
        }
    }
    /// <summary>Grid with utilisation labels on the left axis and memory in GB on the right axis.</summary>
    /// <param name="note">Fixed text shown before the right axis maximum.</param>
    void DrawAxes(Rectangle chart, float? memoryTotalGb, string note)
    {
        DrawGrid(chart);
        int middle = chart.Y + chart.Height / 2 + 1;
        _g.DrawString("100%", _small, _muted, chart.X + 1, chart.Y + 1, _left);
        _g.DrawString("50%", _small, _muted, chart.X + 1, middle, _left);
        if (memoryTotalGb is { } total)
        {
            var axis = new RectangleF(chart.X, 0, chart.Width - 1, 14);
            _g.DrawString($"{note} {total:0}G".TrimStart(), _small, _muted, axis with { Y = chart.Y + 1 }, _right);
            _g.DrawString($"{total / 2:0}G", _small, _muted, axis with { Y = middle }, _right);
        }
    }
    /// <summary>A tick below the chart every 30 seconds.</summary>
    void DrawTimeAxis(Rectangle chart)
    {
        using var pen = new Pen(Muted);
        const int intervals = DashboardState.HistoryLength / 30;
        for (int i = 0; i <= intervals; i++)
        {
            int x = chart.X + chart.Width * i / intervals;
            _g.DrawLine(pen, x, chart.Bottom + 1, x, chart.Bottom + 4);
        }
    }

    /// <summary>
    /// Draws pieces left to right on one line: a label in its own colour, or a white value followed by
    /// its unit in a smaller, lighter face and the given colour.
    /// Each piece owns a slot as wide as its widest expected value, so pieces stay put while values change.
    /// Returns the x after the last slot.
    /// </summary>
    float DrawInline(float x, int y, params (string Text, string Unit, string Widest, Font Font, Brush Brush)[] parts)
    {
        foreach (var (text, unit, widest, font, brush) in parts)
        {
            float slot = _g.MeasureString(widest, font, 300, _left).Width;
            if (unit.Length == 0)
            {
                _g.DrawString(text, font, brush, x, y, _left);
            }
            else
            {
                // Right-aligned against the unit, which keeps both in place whatever the digit count.
                _g.DrawString(text, font, _text, new RectangleF(x - 20, y - 1, slot + 20, 14), _right);
                if (text.Length > 0) _g.DrawString(unit, _tiny, brush, x + slot, y + 1, _left);
                x += _g.MeasureString(unit, _tiny, 300, _left).Width;
            }
            x += slot + 3;
        }
        return x;
    }
    void DrawCard(Rectangle rect)
    {
        using var fill = new SolidBrush(Card);
        using var border = new Pen(Border);
        _g.FillRectangle(fill, rect);
        _g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
    }

    /// <summary>Lines at 0, 50 and 100 % of the axis.</summary>
    void DrawGrid(Rectangle chart)
    {
        using var pen = new Pen(Grid);
        for (int i = 0; i <= 2; i++)
        {
            int y = chart.Y + chart.Height * i / 2;
            _g.DrawLine(pen, chart.X, y, chart.Right, y);
        }
    }

    /// <summary>Plots a series against an axis running from 0 to <paramref name="max"/>.</summary>
    /// <param name="downward">Measure from the top edge down instead of from the bottom edge up.</param>
    /// <param name="compress">Square-root scale, so small values stay visible next to spikes.</param>
    void DrawSeries(
        Rectangle chart, History history, float max, Color color,
        bool fill = false, bool downward = false, bool compress = false)
    {
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f) { LineJoin = LineJoin.Round };
        using var area = new SolidBrush(Color.FromArgb(40, color));

        float step = (float)chart.Width / (history.Capacity - 1);
        int baseline = downward ? chart.Y : chart.Bottom;
        var run = new List<PointF>();

        void Flush()
        {
            if (run.Count > 1)
            {
                if (fill)
                {
                    PointF[] polygon = [.. run, new(run[^1].X, baseline), new(run[0].X, baseline)];
                    _g.FillPolygon(area, polygon);
                }
                _g.DrawLines(pen, run.ToArray());
            }
            run.Clear();
        }

        for (int i = 0; i < history.Capacity; i++)
        {
            float v = history[i];
            if (float.IsNaN(v))
            {
                Flush();
                continue;
            }
            float ratio = Math.Clamp(v / max, 0, 1);
            if (compress) ratio = MathF.Sqrt(ratio);
            float offset = ratio * chart.Height;
            run.Add(new PointF(chart.X + i * step, downward ? baseline + offset : baseline - offset));
        }
        Flush();
        _g.SmoothingMode = SmoothingMode.None;
    }

    static (string Value, string Unit) Speed(double bytesPerSecond) => bytesPerSecond >= 1048576
        ? ((bytesPerSecond / 1048576).ToString("0.00"), "MB/s")
        : ((bytesPerSecond / 1024).ToString("0.00"), "KB/s");

    /// <summary>Throughput in megabytes per second, or kilobytes below one megabyte.</summary>
    static (string Value, string Unit) Rate(double? bytesPerSecond) => bytesPerSecond switch
    {
        null => ("", "M"),
        >= 1024000 => ((bytesPerSecond.Value / 1048576).ToString("0.00"), "M"),
        _ => ((bytesPerSecond.Value / 1024).ToString("0.00"), "K"),
    };

    static string Percent(float? value) => value is { } v ? $"{v:0}%" : "—";

    static string Number(float? value, string format = "0") => value?.ToString(format) ?? "";

    static string Unit(float? value, string unit) => value is { } v ? $"{v:0}{unit}" : "";

    static Color VendorColor(string? name) => name switch
    {
        not null when name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => NvidiaColor,
        not null when name.Contains("AMD", StringComparison.OrdinalIgnoreCase) => AmdColor,
        not null when name.Contains("Intel", StringComparison.OrdinalIgnoreCase) => IntelColor,
        _ => Text,
    };

    static string ShortGpuName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "GPU";
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..];
        }
        return name;
    }

    /// <summary>Copies the rendered frame into an RGB565 buffer.</summary>
    public unsafe void CopyRgb565(ushort[] target)
    {
        var data = _bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                uint* row = (uint*)(data.Scan0 + y * data.Stride);
                int offset = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    uint p = row[x];
                    target[offset + x] = (ushort)(((p >> 8) & 0xF800) | ((p >> 5) & 0x07E0) | ((p >> 3) & 0x001F));
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _g.Dispose();
        _bitmap.Dispose();
        _title.Dispose();
        _label.Dispose();
        _value.Dispose();
        _small.Dispose();
        _smallBold.Dispose();
        _number.Dispose();
        _tiny.Dispose();
        _text.Dispose();
        _muted.Dispose();
        _left.Dispose();
        _center.Dispose();
        _right.Dispose();
    }
}
