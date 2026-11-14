using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ScreenUpdater;
using ScreenUpdater.Lcd;
using ScreenUpdater.Metrics;
using ScreenUpdater.Ui;

var baseDirectory = AppContext.BaseDirectory;
var logPath = Path.Combine(baseDirectory, "updater.log");
var logLock = new object();

void Log(string message)
{
    lock (logLock)
    {
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1 << 20) File.Delete(logPath);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}

// --preview <file.png> [seconds]: render to an image instead of the screen.
string? previewPath = args.Length >= 2 && args[0] == "--preview" ? args[1] : null;
int previewSeconds = args.Length >= 3 ? int.Parse(args[2]) : 5;

using var instance = new Mutex(true, @"Global\ScreenUpdater", out bool first);
if (!first && previewPath is null) return;

var config = Config.Load(baseDirectory);
bool landscape = config.Orientation is Orientation.Landscape or Orientation.ReverseLandscape;
int width = landscape ? 480 : 320;
int height = landscape ? 320 : 480;

using var frames = new FrameMonitor(config.PresentMonPath, baseDirectory, Log);
using var sensors = new Sensors();
using var power = new DisplayPower();
using var dashboard = new Dashboard(width, height);
var state = new DashboardState();

void Sample()
{
    var foreground = ForegroundApp.Get();
    state.AppName = foreground?.Name;
    state.FramesAvailable = frames.Available;
    state.Frames = foreground is { } fg ? frames.GetStats(fg.ProcessId, fg.Name) : null;
    state.Hardware = sensors.Sample();

    state.Fps.Add((float?)state.Frames?.Fps);
    state.LowFps.Add(state.Frames is { } f ? (float)(1000 / f.P99Ms) : null);
    state.Cpu.Add(state.Hardware.CpuLoad);
    for (int i = 0; i < state.Gpus.Length; i++)
        state.Gpus[i].Add(i < state.Hardware.Gpus.Count ? state.Hardware.Gpus[i].Load : null);
    state.Mem.Add(state.Hardware.MemLoad);
}

// Slow orbit within ±2 px, one step per minute.
static Point PixelShift(TimeSpan elapsed)
{
    int step = (int)elapsed.TotalMinutes;
    return new Point(
        (int)Math.Round(2 * Math.Sin(2 * Math.PI * step / 16)),
        (int)Math.Round(2 * Math.Sin(2 * Math.PI * step / 12)));
}

if (previewPath is not null)
{
    for (int i = 0; i < previewSeconds; i++)
    {
        Sample();
        Thread.Sleep(config.IntervalMs);
    }
    Sample();
    dashboard.Render(state, Point.Empty).Save(previewPath, ImageFormat.Png);
    return;
}

Log("started");
var current = new ushort[width * height];
var timer = Stopwatch.StartNew();

while (true)
{
    try
    {
        var portName = config.Port ?? TuringLcd.FindPort()
            ?? throw new IOException("screen not found (USB VID_1A86 PID_5722)");
        using var lcd = new TuringLcd(portName, config.Orientation);
        lcd.SetBrightness(config.Brightness);
        Log($"connected on {portName}");

        ushort[]? shown = null;
        bool screenOn = true;

        while (true)
        {
            long tick = timer.ElapsedMilliseconds;

            if (power.IsOn != screenOn)
            {
                screenOn = power.IsOn;
                if (screenOn)
                {
                    lcd.ScreenOn();
                    lcd.SetBrightness(config.Brightness);
                    shown = null;
                }
                else
                {
                    lcd.ScreenOff();
                }
                Log(screenOn ? "display on" : "display off");
            }

            if (screenOn)
            {
                Sample();
                dashboard.Render(state, PixelShift(timer.Elapsed));
                dashboard.CopyRgb565(current);
                long drawStart = timer.ElapsedMilliseconds;
                foreach (var rect in FrameDiffer.Diff(shown, current, width, height))
                    lcd.DrawRegion(current, rect.X, rect.Y, rect.Width, rect.Height);
                if (shown is null) Log($"full frame sent in {timer.ElapsedMilliseconds - drawStart} ms");
                shown ??= new ushort[current.Length];
                current.CopyTo(shown, 0);
            }

            int wait = config.IntervalMs - (int)(timer.ElapsedMilliseconds - tick);
            if (wait > 0) Thread.Sleep(wait);
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
    {
        Log($"screen error: {e.Message}; retrying");
        Thread.Sleep(5000);
    }
}
