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
}

public sealed class DashboardState
{
    public const int HistoryLength = 120;

    public string? AppName;
    public bool FramesAvailable;
    public FrameStats? Frames;
    public HardwareSample? Hardware;
    public WeatherReport? Weather;

    public readonly History Cpu = new(HistoryLength);
    public readonly History Mem = new(HistoryLength);
    public readonly History[] GpuLoad = NewHistories();
    public readonly History[] GpuMemory = NewHistories();

    static History[] NewHistories() =>
        [.. Enumerable.Range(0, Sensors.GpuCount).Select(_ => new History(HistoryLength))];
}

public sealed class Dashboard : IDisposable
{
    static readonly Color Background = Color.FromArgb(0x1c, 0x1c, 0x1c);
    static readonly Color Card = Color.FromArgb(0x2b, 0x2b, 0x2b);
    static readonly Color Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
    static readonly Color Grid = Color.FromArgb(0x45, 0x45, 0x45);
    static readonly Color Text = Color.White;
    static readonly Color Muted = Color.FromArgb(0xa0, 0xa0, 0xa0);
    static readonly Color CpuColor = Color.FromArgb(0x4c, 0xc2, 0xff);
    static readonly Color MemColor = Color.FromArgb(0xb4, 0xa7, 0xf5);
    static readonly Color[] GpuColors = [Color.FromArgb(0x5f, 0xd0, 0xb8), Color.FromArgb(0xf0, 0xa5, 0x60)];

    static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    const int Width = TuringLcd.Width;
    const int Height = TuringLcd.Height;
    const int Pad = 6;
    const int TopY = 26;
    const int TopHeight = 130;
    const int FpsWidth = 152;

    readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    readonly Graphics _g;

    readonly Font _title = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _label = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _value = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _small = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _big = new("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel);

    readonly SolidBrush _text = new(Text);
    readonly SolidBrush _muted = new(Muted);

    readonly StringFormat _left = new(StringFormat.GenericTypographic)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
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

        int inner = Width - 2 * Pad;
        DrawFpsCard(new Rectangle(Pad, TopY, FpsWidth, TopHeight), state);
        DrawSystemCard(Rectangle.FromLTRB(Pad + FpsWidth + Pad, TopY, Width - Pad, TopY + TopHeight), state);

        int y = TopY + TopHeight + Pad;
        int w = (inner - (Sensors.GpuCount - 1) * Pad) / Sensors.GpuCount;
        for (int i = 0; i < Sensors.GpuCount; i++)
        {
            var gpu = state.Hardware is { } hw && i < hw.Gpus.Count ? hw.Gpus[i] : null;
            DrawGpuCard(new Rectangle(Pad + i * (w + Pad), y, w, Height - y - Pad), gpu,
                GpuColors[i % GpuColors.Length], state.GpuLoad[i], state.GpuMemory[i]);
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

    void DrawFpsCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        int x = rect.X + 10;
        int y = rect.Y + 6;
        int w = rect.Width - 20;

        _g.DrawString("FPS", _label, _muted, new RectangleF(x, y, w, 16), _right);
        _g.DrawString(state.AppName ?? "", _label, _muted, new RectangleF(x, y, w - 30, 16), _left);

        var frames = state.Frames;
        _g.DrawString(frames is { } f ? f.Fps.ToString("0") : "—", _big, frames is null ? _muted : _text, x - 3, y + 12, _left);

        int rowsY = y + 70;
        if (!state.FramesAvailable)
        {
            _g.DrawString("PresentMon 未运行", _label, _muted, x, rowsY + 17, _left);
            return;
        }

        var rows = new (string Name, double? Ms)[]
        {
            ("P50", frames?.P50Ms), ("P95", frames?.P95Ms), ("P99", frames?.P99Ms),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            int ry = rowsY + i * 17;
            _g.DrawString(rows[i].Name, _label, _muted, x, ry, _left);
            if (rows[i].Ms is { } ms)
            {
                _g.DrawString((1000 / ms).ToString("0"), _value, _text, x + 34, ry, _left);
                _g.DrawString($"{ms:0.0} ms", _label, _muted, new RectangleF(x, ry, w, 16), _right);
            }
            else
            {
                _g.DrawString("—", _value, _muted, x + 34, ry, _left);
            }
        }
    }

    void DrawSystemCard(Rectangle rect, DashboardState state)
    {
        DrawCard(rect);
        var hw = state.Hardware;
        int y = rect.Y + 6;

        float x = rect.X + 10;
        x = DrawLegend(x, y, CpuColor, "CPU", Percent(hw?.CpuLoad), "");
        DrawLegend(x + 14, y, MemColor, "内存", Percent(hw?.MemLoad),
            hw is null ? "" : $"{hw.MemUsedGb:0.0} / {hw.MemTotalGb:0.0} GB");

        var chart = Rectangle.FromLTRB(rect.X + 10, y + 22, rect.Right - 10, rect.Bottom - 8);
        DrawGrid(chart);
        _g.DrawString("100%", _small, _muted, chart.X + 1, chart.Y + 1, _left);
        _g.DrawString("50%", _small, _muted, chart.X + 1, chart.Y + chart.Height / 2 + 1, _left);
        DrawSeries(chart, state.Mem, MemColor);
        DrawSeries(chart, state.Cpu, CpuColor);
    }

    void DrawGpuCard(Rectangle rect, GpuSample? gpu, Color color, History load, History memory)
    {
        DrawCard(rect);
        int x = rect.X + 10;
        int y = rect.Y + 6;
        int w = rect.Width - 20;

        using (var swatch = new SolidBrush(color))
            _g.FillRectangle(swatch, x, y + 4, 8, 8);
        _g.DrawString(ShortGpuName(gpu?.Name), _label, _text, new RectangleF(x + 13, y, w - 13, 16), _left);
        if (gpu?.Temp is { } temp)
            _g.DrawString($"{temp:0}°C", _value, _text, new RectangleF(x, y, w, 16), _right);

        int half = (rect.Bottom - 6 - (y + 18) - 4) / 2;

        float? memoryRatio = gpu is { VramUsedMb: { } used, VramTotalMb: > 0 } ? used / gpu.VramTotalMb * 100 : null;
        string memoryDetail = gpu is { VramUsedMb: { } u } ? $"{u / 1024:0.0} / {gpu.VramTotalMb / 1024:0.0} GB" : "";

        DrawGpuChart(new Rectangle(x, y + 18, w, half), "利用率", gpu?.Load, gpu?.Engine ?? "", color, load);
        DrawGpuChart(new Rectangle(x, y + 18 + half + 4, w, half), "显存", memoryRatio, memoryDetail, MemColor, memory);
    }

    void DrawGpuChart(Rectangle rect, string name, float? value, string detail, Color color, History history)
    {
        _g.DrawString(name, _small, _muted, rect.X, rect.Y, _left);
        float nameW = _g.MeasureString(name, _small, 200, _left).Width;
        _g.DrawString(Percent(value), _value, _text, rect.X + nameW + 5, rect.Y - 2, _left);
        _g.DrawString(detail, _small, _muted, new RectangleF(rect.X, rect.Y, rect.Width, 14), _right);

        var chart = Rectangle.FromLTRB(rect.X, rect.Y + 15, rect.Right, rect.Bottom);
        DrawGrid(chart);
        DrawSeries(chart, history, color);
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

        x += 6;
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

    /// <summary>Lines at 0, 50 and 100 %.</summary>
    void DrawGrid(Rectangle chart)
    {
        using var pen = new Pen(Grid);
        for (int i = 0; i <= 2; i++)
        {
            int y = chart.Y + chart.Height * i / 2;
            _g.DrawLine(pen, chart.X, y, chart.Right, y);
        }
    }

    /// <summary>Plots a 0–100 series across the full chart height.</summary>
    void DrawSeries(Rectangle chart, History history, Color color)
    {
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f) { LineJoin = LineJoin.Round };
        using var area = new SolidBrush(Color.FromArgb(40, color));

        float step = (float)chart.Width / (history.Capacity - 1);
        var run = new List<PointF>();

        void Flush()
        {
            if (run.Count > 1)
            {
                PointF[] polygon = [.. run, new(run[^1].X, chart.Bottom), new(run[0].X, chart.Bottom)];
                _g.FillPolygon(area, polygon);
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
            float ratio = Math.Clamp(v / 100, 0, 1);
            run.Add(new PointF(chart.X + i * step, chart.Bottom - ratio * chart.Height));
        }
        Flush();
        _g.SmoothingMode = SmoothingMode.None;
    }

    static string Percent(float? value) => value is { } v ? $"{v:0}%" : "—";

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
        _big.Dispose();
        _text.Dispose();
        _muted.Dispose();
        _left.Dispose();
        _right.Dispose();
    }
}
