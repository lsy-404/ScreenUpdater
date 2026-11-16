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
const int width = TuringLcd.Width;
const int height = TuringLcd.Height;
const int ScrubHeight = 16;

using var frames = new FrameMonitor(
    config.PresentMonPath, baseDirectory, previewPath is null ? "ScreenUpdater" : "ScreenUpdaterPreview", Log);
using var sensors = new Sensors();
using var system = new SystemStats();
using var power = new DisplayPower();
using var dashboard = new Dashboard();
using var weather = config is { Latitude: { } latitude, Longitude: { } longitude }
    ? new Weather(latitude, longitude, Log)
    : null;
var state = new DashboardState();

void Sample()
{
    var foreground = ForegroundApp.Get();
    state.FramesAvailable = frames.Available;
    state.Frames = foreground is { } fg ? frames.GetStats(fg.ProcessId, fg.Name) : null;
    state.FramesSupported = foreground is { } app && frames.HasPresented(app.Name);
    state.RefreshRates = Displays.RefreshRates();
    state.Weather = weather?.Latest;
    state.Fps.Add(state.FramesAvailable ? (float)(state.Frames?.Fps ?? 0) : null);

    var stats = state.System = system.Sample();
    state.Download.Add((float)stats.DownloadBps);
    state.Upload.Add((float)stats.UploadBps);

    var hardware = state.Hardware = sensors.Sample();
    state.Cpu.Add(hardware.CpuLoad);
    state.Mem.Add(hardware.MemUsedGb / hardware.MemTotalGb * 100);
    state.Integrated.Add(hardware.IntegratedLoad);
    for (int i = 0; i < hardware.Gpus.Count; i++)
        state.Gpus[i].Add(hardware.Gpus[i]);
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
        using var lcd = new TuringLcd(portName, config.Flip);
        lcd.SetBrightness(config.Brightness);
        Log($"connected on {portName}");

        ushort[]? shown = null;
        int scrubBand = 0;
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
                if (shown is null)
                {
                    Log($"full frame sent in {timer.ElapsedMilliseconds - drawStart} ms");
                }
                else
                {
                    // Re-send one band per tick so anything the screen dropped in transit is repaired.
                    lcd.DrawRegion(current, 0, scrubBand * ScrubHeight, width, ScrubHeight);
                    scrubBand = (scrubBand + 1) % (height / ScrubHeight);
                }
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
