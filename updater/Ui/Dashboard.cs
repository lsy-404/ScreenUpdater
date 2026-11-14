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

    /// <summary>Accumulated utilisation, used to rank engines by how much they are used.</summary>
    public double Total;
}

public sealed class GpuState
{
    public readonly History Load = new(DashboardState.HistoryLength);

    /// <summary>Dedicated memory in use, as a percentage of the adapter's total.</summary>
    public readonly History Memory = new(DashboardState.HistoryLength);
    public readonly Dictionary<string, EngineTrack> Engines = [];

    public void Add(GpuSample sample)
    {
        Load.Add(sample.Load);
        Memory.Add(sample.VramUsedGb / sample.VramTotalGb * 100);
        foreach (var engine in sample.Engines)
        {
            if (!Engines.TryGetValue(engine.Type, out var track))
                Engines[engine.Type] = track = new EngineTrack(engine.Index);
            track.History.Add(engine.Load);
            track.Total += engine.Load;
        }
    }
}

public sealed class DashboardState
{
    public const int HistoryLength = 120;

    public bool FramesAvailable;
    public FrameStats? Frames;
    public HardwareSample? Hardware;
    public SystemSample? System;
    public WeatherReport? Weather;

    public readonly History Fps = new(HistoryLength);
    public readonly History Cpu = new(HistoryLength);
    public readonly History Mem = new(HistoryLength);
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
    static readonly Color[] GpuColors = [Color.FromArgb(0x5f, 0xd0, 0xb8), Color.FromArgb(0xf0, 0xa5, 0x60)];

    static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    const int Width = TuringLcd.Width;
    const int Height = TuringLcd.Height;
    const int Pad = 6;
    const int TopY = 26;
    const int TopHeight = 112;
    const int LeftWidth = 140;
    const int FpsHeight = 68;
    const int NetworkWidth = 110;
    const int EngineCharts = 4;

    readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    readonly Graphics _g;

    readonly Font _title = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _label = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _value = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _small = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _smallBold = new("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);

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
            DrawGpuCard(new Rectangle(Pad + i * (w + Pad), y, w, Height - y - Pad), gpu,
                GpuColors[i % GpuColors.Length], state.Gpus[i]);
        }

        return _bitmap;
    }

    void DrawHeader(DashboardState state)
    {
        var now = DateTime.Now;
        var clock = $"{now.Month}月{now.Day}日 {Weekdays[(int)now.DayOfWeek]} {now:HH:mm:ss}";
        _g.DrawString(clock, _title, _text, Pad + 2, 4, _left);

        if (state.Weather is { } weather)
        {
            var text = $"{weather.Condition} {weather.Temperature:0}°C · 今日 {weather.Low:0}~{weather.High:0}°C";
            _g.DrawString(text, _label, _muted, new RectangleF(0, 5, Width - Pad - 2, 18), _right);
        }
    }

    /// <summary>Frame rate of the foreground application: history on top, percentiles underneath.</summary>
    void DrawFpsCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        int x = rect.X + 8;
        int y = rect.Y + 4;
        int w = rect.Width - 16;

        _g.DrawString("FPS", _small, _muted, x, y, _left);

        var chart = new Rectangle(x, y + 14, w, rect.Height - 38);
        DrawGrid(chart);
        if (!state.FramesAvailable)
        {
            _g.DrawString("PresentMon 未运行", _small, _muted, new RectangleF(x, chart.Y + chart.Height / 2 - 6, w, 12), _center);
            return;
        }

        float max = MathF.Max(60, MathF.Ceiling(state.Fps.Max() / 30) * 30);
        _g.DrawString(max.ToString("0"), _small, _muted, new RectangleF(x, y, w, 12), _right);
        DrawSeries(chart, state.Fps, max, FpsColor, fill: true);

        var frames = state.Frames;
        var rows = new (string Name, double? Ms)[]
        {
            ("P50", frames?.P50Ms), ("P95", frames?.P95Ms), ("P99", frames?.P99Ms),
        };
        int third = w / rows.Length;
        for (int i = 0; i < rows.Length; i++)
        {
            int cx = x + i * third;
            _g.DrawString(rows[i].Name, _small, _muted, cx, chart.Bottom + 4, _left);
            _g.DrawString(rows[i].Ms is { } ms ? (1000 / ms).ToString("0") : "—", _smallBold, _text, cx + 20, chart.Bottom + 4, _left);
        }
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
            _g.DrawString(columns[i].Name, _small, _muted, x + i * w, rect.Y + 4, _left);
            _g.DrawString(columns[i].Value?.ToString() ?? "—", _value, _text, x + i * w, rect.Y + 17, _left);
        }
    }

    void DrawSystemCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        var hw = state.Hardware;
        int x = rect.X + 8;
        int y = rect.Y + 5;

        DrawLegend(x, y, CpuColor, "CPU", Percent(hw?.CpuLoad),
            hw is null ? "" : Join($"{hw.CpuGhz:0.00}GHz", Unit(hw.CpuPower, "W"), Unit(hw.CpuTemp, "°C")));
        DrawLegend(x, y + 15, MemColor, "内存", Percent(hw?.MemLoad),
            hw is null ? "" : Join($"{hw.MemUsedGb:0.0}/{hw.MemTotalGb:0.0}G", Unit(state.System?.MemoryMhz, "MHz")));

        var chart = Rectangle.FromLTRB(x, y + 35, rect.Right - 8, rect.Bottom - 19);
        DrawAxes(chart, hw?.MemTotalGb);
        DrawSeries(chart, state.Mem, 100, MemColor, dashed: true);
        DrawSeries(chart, state.Cpu, 100, CpuColor, fill: true);
        DrawTimeAxis(chart, ["-2m", "-1m", "现在"]);
    }

    void DrawNetworkCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        int x = rect.X + 8;
        int y = rect.Y + 5;
        int w = rect.Width - 16;

        DrawSpeed(x, y, w, "↓", DownColor, state.System?.DownloadBps);
        DrawSpeed(x, y + 15, w, "↑", UpColor, state.System?.UploadBps);

        var chart = Rectangle.FromLTRB(x, y + 46, rect.Right - 8, rect.Bottom - 8);
        float max = MathF.Max(MathF.Max(state.Download.Max(), state.Upload.Max()), 102400);
        var (scale, unit) = Speed(max);
        _g.DrawString($"{scale} {unit}", _small, _muted, new RectangleF(x, chart.Y - 13, w, 12), _right);
        DrawGrid(chart);
        DrawSeries(chart, state.Upload, max, UpColor);
        DrawSeries(chart, state.Download, max, DownColor, fill: true);
    }

    void DrawSpeed(int x, int y, int width, string arrow, Color color, double? bytesPerSecond)
    {
        using (var brush = new SolidBrush(color))
            _g.DrawString(arrow, _value, brush, x, y, _left);
        if (bytesPerSecond is not { } bps) return;

        var (value, unit) = Speed(bps);
        _g.DrawString(unit, _small, _muted, new RectangleF(x, y + 2, width, 12), _right);
        _g.DrawString(value, _value, _text, new RectangleF(x, y, width - 27, 16), _right);
    }

    void DrawGpuCard(Rectangle rect, GpuSample? gpu, Color color, GpuState state)
    {
        DrawCard(rect);
        int x = rect.X + 10;
        int y = rect.Y + 5;
        int w = rect.Width - 20;

        float nameEnd = DrawLegend(x, y, color, ShortGpuName(gpu?.Name), Percent(gpu?.Load), "");
        if (gpu?.Temp is { } temp)
            _g.DrawString($"{temp:0}°C", _value, _text, new RectangleF(nameEnd, y, rect.Right - 10 - nameEnd, 16), _right);

        if (gpu is not null)
        {
            var detail = Join(
                gpu.VramUsedGb is { } used ? $"显存 {used:0.0}/{gpu.VramTotalGb:0.0}G" : "",
                Unit(gpu.PowerW, "W"),
                gpu.ClockMhz is { } clock ? $"{clock / 1000:0.00}GHz" : "");
            _g.DrawString(detail, _small, _muted, new RectangleF(x, y + 17, w, 14), _left);
        }

        var chart = new Rectangle(x, y + 32, w, 40);
        DrawAxes(chart, gpu?.VramTotalGb);
        DrawSeries(chart, state.Memory, 100, MemColor, dashed: true);
        DrawSeries(chart, state.Load, 100, color, fill: true);
        DrawTimeAxis(chart, ["-2m", "-1m", "现在"]);

        // The most used engine types, kept in the adapter's own order so cells do not jump around.
        var engines = state.Engines
            .OrderByDescending(e => e.Value.Total).ThenBy(e => e.Value.Index)
            .Take(EngineCharts)
            .OrderBy(e => e.Value.Index)
            .ToList();

        const int gap = 6;
        int top = chart.Bottom + 16;
        int cellW = (w - gap) / 2;
        int cellH = (rect.Bottom - 6 - top - 3) / 2;
        for (int i = 0; i < engines.Count; i++)
        {
            var (type, track) = engines[i];
            var cell = new Rectangle(x + i % 2 * (cellW + gap), top + i / 2 * (cellH + 3), cellW, cellH);
            float? load = gpu?.Engines.FirstOrDefault(e => e.Type == type)?.Load;
            _g.DrawString(type, _small, _muted, new RectangleF(cell.X, cell.Y, cell.Width - 26, 12), _left);
            _g.DrawString(Percent(load), _smallBold, _text, new RectangleF(cell.X, cell.Y, cell.Width, 12), _right);

            var engineChart = Rectangle.FromLTRB(cell.X, cell.Y + 13, cell.Right, cell.Bottom);
            DrawGrid(engineChart);
            DrawSeries(engineChart, track.History, 100, color, fill: true);
        }
    }

    /// <summary>Grid with utilisation labels on the left axis and memory in GB on the right axis.</summary>
    void DrawAxes(Rectangle chart, float? memoryTotalGb)
    {
        DrawGrid(chart);
        int middle = chart.Y + chart.Height / 2 + 1;
        _g.DrawString("100%", _small, _muted, chart.X + 1, chart.Y + 1, _left);
        _g.DrawString("50%", _small, _muted, chart.X + 1, middle, _left);
        if (memoryTotalGb is { } total)
        {
            var axis = new RectangleF(chart.X, 0, chart.Width - 1, 14);
            _g.DrawString($"{total:0}G", _small, _muted, axis with { Y = chart.Y + 1 }, _right);
            _g.DrawString($"{total / 2:0}G", _small, _muted, axis with { Y = middle }, _right);
        }
    }

    /// <summary>Evenly spaced ticks below the chart, newest sample on the right.</summary>
    void DrawTimeAxis(Rectangle chart, string[] labels)
    {
        using var pen = new Pen(Grid);
        for (int i = 0; i < labels.Length; i++)
        {
            int x = chart.X + chart.Width * i / (labels.Length - 1);
            _g.DrawLine(pen, x, chart.Bottom, x, chart.Bottom + 3);

            var format = i == 0 ? _left : i == labels.Length - 1 ? _right : _center;
            float left = i == 0 ? x : i == labels.Length - 1 ? x - 40 : x - 20;
            _g.DrawString(labels[i], _small, _muted, new RectangleF(left, chart.Bottom + 3, 40, 12), format);
        }
    }

    /// <summary>Swatch, name, bold value and muted detail on one line; returns the x after it.</summary>
    float DrawLegend(float x, int y, Color color, string name, string value, string detail)
    {
        using (var swatch = new SolidBrush(color))
            _g.FillRectangle(swatch, x, y + 4, 8, 8);
        x += 13;
        _g.DrawString(name, _label, _text, x, y, _left);
        x += _g.MeasureString(name, _label, 200, _left).Width + 5;
        _g.DrawString(value, _value, _text, x, y, _left);
        x += _g.MeasureString(value, _value, 200, _left).Width;
        if (detail.Length == 0) return x;

        x += 5;
        _g.DrawString(detail, _small, _muted, x, y + 2, _left);
        return x + _g.MeasureString(detail, _small, 300, _left).Width;
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
    void DrawSeries(Rectangle chart, History history, float max, Color color, bool dashed = false, bool fill = false)
    {
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f) { LineJoin = LineJoin.Round };
        if (dashed) pen.DashPattern = [3, 3];
        using var area = new SolidBrush(Color.FromArgb(40, color));

        float step = (float)chart.Width / (history.Capacity - 1);
        var run = new List<PointF>();

        void Flush()
        {
            if (run.Count > 1)
            {
                if (fill)
                {
                    PointF[] polygon = [.. run, new(run[^1].X, chart.Bottom), new(run[0].X, chart.Bottom)];
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
            run.Add(new PointF(chart.X + i * step, chart.Bottom - ratio * chart.Height));
        }
        Flush();
        _g.SmoothingMode = SmoothingMode.None;
    }

    static (string Value, string Unit) Speed(double bytesPerSecond) => bytesPerSecond >= 1048576
        ? ((bytesPerSecond / 1048576).ToString("0.00"), "MB/s")
        : ((bytesPerSecond / 1024).ToString("0.00"), "KB/s");

    static string Percent(float? value) => value is { } v ? $"{v:0}%" : "—";

    static string Unit(float? value, string unit) => value is { } v ? $"{v:0}{unit}" : "";

    static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));

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
        _text.Dispose();
        _muted.Dispose();
        _left.Dispose();
        _center.Dispose();
        _right.Dispose();
    }
}
