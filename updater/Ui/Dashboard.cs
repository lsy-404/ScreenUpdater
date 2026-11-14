using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
            if (!float.IsNaN(v) && v > max) max = v;
        return max;
    }
}

public sealed class DashboardState
{
    public const int HistoryLength = 120;

    public string? AppName;
    public bool FramesAvailable;
    public FrameStats? Frames;
    public HardwareSample? Hardware;

    public readonly History Fps = new(HistoryLength);
    public readonly History LowFps = new(HistoryLength);
    public readonly History Cpu = new(HistoryLength);
    public readonly History[] Gpus = [.. Enumerable.Range(0, Sensors.GpuCount).Select(_ => new History(HistoryLength))];
    public readonly History Mem = new(HistoryLength);
}

public sealed class Dashboard : IDisposable
{
    static readonly Color Background = Color.FromArgb(0x1c, 0x1c, 0x1c);
    static readonly Color Card = Color.FromArgb(0x2b, 0x2b, 0x2b);
    static readonly Color Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
    static readonly Color Grid = Color.FromArgb(0x45, 0x45, 0x45);
    static readonly Color Text = Color.White;
    static readonly Color Muted = Color.FromArgb(0xa0, 0xa0, 0xa0);
    static readonly Color FpsColor = Color.FromArgb(0x6c, 0xcb, 0x5f);
    static readonly Color LowColor = Color.FromArgb(0xfc, 0xe1, 0x00);
    static readonly Color CpuColor = Color.FromArgb(0x4c, 0xc2, 0xff);
    static readonly Color MemColor = Color.FromArgb(0xb4, 0xa7, 0xf5);
    static readonly Color[] GpuColors = [Color.FromArgb(0x5f, 0xd0, 0xb8), Color.FromArgb(0xf0, 0xa5, 0x60)];

    const int Pad = 6;
    const int HeaderHeight = 26;

    readonly int _width;
    readonly int _height;
    readonly Bitmap _bitmap;
    readonly Graphics _g;

    readonly Font _title = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _label = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _value = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Font _small = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly Font _big = new("Segoe UI", 46, FontStyle.Bold, GraphicsUnit.Pixel);

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

    public Dashboard(int width, int height)
    {
        _width = width;
        _height = height;
        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
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

        bool landscape = _width > _height;
        int inner = _width - 2 * Pad;
        int top = HeaderHeight + 2;

        var fpsCard = new Rectangle(Pad, top, inner, landscape ? 128 : 178);
        DrawFpsCard(fpsCard, state, landscape);

        int y = fpsCard.Bottom + Pad;
        var hw = state.Hardware;
        var cards = new List<(string Name, Color Color, float? Load, string Detail, History History)>
        {
            ("CPU", CpuColor, hw?.CpuLoad, Join(Unit(hw?.CpuTemp, "°C"), Unit(hw?.CpuPower, "W")), state.Cpu),
        };
        for (int i = 0; i < state.Gpus.Length; i++)
        {
            var gpu = hw is not null && i < hw.Gpus.Count ? hw.Gpus[i] : null;
            cards.Add((ShortGpuName(gpu?.Name), GpuColors[i % GpuColors.Length], gpu?.Load,
                Join(Unit(gpu?.Temp, "°C"), Unit(gpu?.Power, "W"), Gigabytes(gpu?.VramUsedMb / 1024, "G")),
                state.Gpus[i]));
        }
        cards.Add(("内存", MemColor, hw?.MemLoad,
            hw?.MemUsedGb is { } used && hw.MemTotalGb is { } total ? $"{used:0.0} / {total:0.0} GB" : "", state.Mem));

        int columns = landscape ? 2 : 1;
        int rows = (cards.Count + columns - 1) / columns;
        int w = (inner - (columns - 1) * Pad) / columns;
        int h = (_height - y - Pad - (rows - 1) * Pad) / rows;
        for (int i = 0; i < cards.Count; i++)
        {
            DrawLoadCard(
                new Rectangle(Pad + i % columns * (w + Pad), y + i / columns * (h + Pad), w, h),
                cards[i]);
        }
        return _bitmap;
    }

    void DrawHeader(DashboardState state)
    {
        using var text = new SolidBrush(Text);
        using var muted = new SolidBrush(Muted);
        var clock = DateTime.Now.ToString("HH:mm");
        _g.DrawString(clock, _label, muted, new RectangleF(0, 6, _width - Pad - 2, 18), _right);
        _g.DrawString(state.AppName ?? "—", _title, text, new RectangleF(Pad + 2, 5, _width - 70, 18), _left);
    }

    void DrawFpsCard(Rectangle rect, DashboardState state, bool landscape)
    {
        DrawCard(rect);
        using var text = new SolidBrush(Text);
        using var muted = new SolidBrush(Muted);

        int x = rect.X + 10;
        int y = rect.Y + 6;
        _g.DrawString("FPS", _label, muted, x, y, _left);

        var frames = state.Frames;
        _g.DrawString(frames is { } f ? f.Fps.ToString("0") : "—", _big, text, x - 3, y + 8, _left);

        var rows = new (string Name, double? Ms)[]
        {
            ("P50", frames?.P50Ms), ("P95", frames?.P95Ms), ("P99", frames?.P99Ms),
        };
        int rowsX = landscape ? x : rect.X + 136;
        int rowsY = landscape ? y + 62 : y + 12;
        int rowsW = landscape ? 126 : rect.Right - 10 - rowsX;
        for (int i = 0; i < rows.Length; i++)
        {
            int ry = rowsY + i * 18;
            _g.DrawString(rows[i].Name, _label, muted, rowsX, ry, _left);
            if (rows[i].Ms is { } ms)
            {
                _g.DrawString((1000 / ms).ToString("0"), _value, text, rowsX + 34, ry, _left);
                _g.DrawString($"{ms:0.0} ms", _label, muted, new RectangleF(rowsX, ry, rowsW, 16), _right);
            }
            else
            {
                _g.DrawString("—", _value, muted, rowsX + 34, ry, _left);
            }
        }

        var chart = landscape
            ? Rectangle.FromLTRB(rect.X + 150, rect.Y + 24, rect.Right - 10, rect.Bottom - 10)
            : Rectangle.FromLTRB(rect.X + 10, rect.Y + 92, rect.Right - 10, rect.Bottom - 10);

        if (!state.FramesAvailable)
        {
            _g.DrawString("PresentMon 未运行", _label, muted,
                new RectangleF(chart.X, chart.Y + chart.Height / 2 - 8, chart.Width, 16), _right);
            return;
        }

        int legendY = chart.Y - 17;
        float lx = chart.Right;
        lx = DrawLegendRight("P99", LowColor, lx, legendY) - 10;
        DrawLegendRight("FPS", FpsColor, lx, legendY);

        float max = MathF.Max(60, MathF.Ceiling(state.Fps.Max() * 1.1f / 30) * 30);
        DrawGrid(chart, 2);
        _g.DrawString(max.ToString("0"), _small, muted, chart.X + 1, chart.Y + 1, _left);
        _g.DrawString((max / 2).ToString("0"), _small, muted, chart.X + 1, chart.Y + chart.Height / 2 + 1, _left);
        DrawSeries(chart, state.LowFps, max, LowColor, fill: false);
        DrawSeries(chart, state.Fps, max, FpsColor, fill: true);
    }

    float DrawLegendRight(string name, Color color, float right, int y)
    {
        using var muted = new SolidBrush(Muted);
        using var swatch = new SolidBrush(color);
        float w = _g.MeasureString(name, _small, 200, _left).Width;
        _g.DrawString(name, _small, muted, right - w, y, _left);
        _g.FillRectangle(swatch, right - w - 11, y + 4, 7, 7);
        return right - w - 11;
    }

    void DrawLoadCard(Rectangle rect, (string Name, Color Color, float? Load, string Detail, History History) card)
    {
        DrawCard(rect);
        using var text = new SolidBrush(Text);
        using var muted = new SolidBrush(Muted);
        using var swatch = new SolidBrush(card.Color);

        bool twoLine = rect.Width < 200;
        int x = rect.X + 8;
        int y = rect.Y + 6;

        _g.FillRectangle(swatch, x, y + 4, 8, 8);
        float nameW = _g.MeasureString(card.Name, _label, 200, _left).Width;
        _g.DrawString(card.Name, _label, text, x + 13, y, _left);
        _g.DrawString(card.Load is { } load ? $"{load:0}%" : "—", _value, text, x + 13 + nameW + 5, y, _left);

        int chartTop;
        if (twoLine)
        {
            _g.DrawString(card.Detail, _small, muted, new RectangleF(x, y + 18, rect.Width - 16, 14), _left);
            chartTop = y + 36;
        }
        else
        {
            _g.DrawString(card.Detail, _small, muted, new RectangleF(x, y + 1, rect.Width - 16, 14), _right);
            chartTop = y + 20;
        }

        var chart = Rectangle.FromLTRB(x, chartTop, rect.Right - 8, rect.Bottom - 8);
        DrawGrid(chart, 2);
        DrawSeries(chart, card.History, 100, card.Color, fill: true);
    }

    void DrawCard(Rectangle rect)
    {
        using var fill = new SolidBrush(Card);
        using var border = new Pen(Border);
        _g.FillRectangle(fill, rect);
        _g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
    }

    void DrawGrid(Rectangle chart, int divisions)
    {
        using var pen = new Pen(Grid);
        for (int i = 0; i <= divisions; i++)
        {
            int y = chart.Y + chart.Height * i / divisions;
            _g.DrawLine(pen, chart.X, y, chart.Right, y);
        }
    }

    void DrawSeries(Rectangle chart, History history, float max, Color color, bool fill)
    {
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f) { LineJoin = LineJoin.Round };
        using var area = new SolidBrush(Color.FromArgb(45, color));

        float step = (float)chart.Width / (history.Capacity - 1);
        var run = new List<PointF>();

        void Flush()
        {
            if (run.Count > 1)
            {
                if (fill)
                {
                    var polygon = new List<PointF>(run)
                    {
                        new(run[^1].X, chart.Bottom),
                        new(run[0].X, chart.Bottom),
                    };
                    _g.FillPolygon(area, polygon.ToArray());
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

    static string Unit(float? value, string unit) => value is { } v ? $"{v:0}{unit}" : "";

    static string Gigabytes(float? value, string unit) => value is { } v ? $"{v:0.0}{unit}" : "";

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
        var data = _bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < _height; y++)
            {
                uint* row = (uint*)(data.Scan0 + y * data.Stride);
                int offset = y * _width;
                for (int x = 0; x < _width; x++)
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
        _left.Dispose();
        _right.Dispose();
    }
}
