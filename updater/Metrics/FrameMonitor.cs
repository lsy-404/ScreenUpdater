using System.Diagnostics;
using System.Globalization;

namespace ScreenUpdater.Metrics;

/// <param name="Fps">Presents per second over the last second.</param>
/// <param name="P50Ms">Median frame time over the window.</param>
public readonly record struct FrameStats(double Fps, double P50Ms, double P95Ms, double P99Ms, int Samples);

/// <summary>Streams per-frame data from the PresentMon console application.</summary>
public sealed class FrameMonitor : IDisposable
{
    const string SessionName = "ScreenUpdater";
    const double WindowSeconds = 10;

    sealed class ProcessFrames
    {
        public required string Application;
        public readonly Queue<(double Time, double Ms)> Frames = new();
    }

    readonly object _lock = new();
    readonly Dictionary<int, ProcessFrames> _processes = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Process? _process;

    // Smallest observed (arrival − trace time): maps trace timestamps onto the local clock.
    double _clockOffset = double.MaxValue;

    public bool Available => _process is { HasExited: false };

    public FrameMonitor(string? configuredPath, string baseDirectory, Action<string> log)
    {
        var exe = Locate(configuredPath, baseDirectory);
        if (exe is null)
        {
            log("PresentMon not found; frame statistics disabled");
            return;
        }

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "--output_stdout", "--no_console_stats", "--stop_existing_session",
                     "--session_name", SessionName,
                 })
            info.ArgumentList.Add(arg);

        try
        {
            _process = Process.Start(info)!;
        }
        catch (Exception e)
        {
            log($"PresentMon failed to start: {e.Message}");
            return;
        }

        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log($"PresentMon: {e.Data}"); };
        _process.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "PresentMon" }.Start();
        log($"PresentMon started: {exe}");
    }

    static string? Locate(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        var candidates = new List<string>();
        var tools = Path.Combine(baseDirectory, "tools");
        if (Directory.Exists(tools))
            candidates.AddRange(Directory.GetFiles(tools, "PresentMon*.exe"));

        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages");
        if (Directory.Exists(packages))
        {
            foreach (var dir in Directory.GetDirectories(packages, "Intel.PresentMon.Console*"))
                candidates.AddRange(Directory.GetFiles(dir, "PresentMon*.exe", SearchOption.AllDirectories));
        }

        return candidates.FirstOrDefault();
    }

    void ReadLoop()
    {
        var reader = _process!.StandardOutput;
        var header = reader.ReadLine();
        if (header is null) return;

        var columns = header.Split(',');
        int appCol = Array.IndexOf(columns, "Application");
        int pidCol = Array.IndexOf(columns, "ProcessID");
        int msCol = Array.IndexOf(columns, "MsBetweenPresents");
        int timeCol = Array.IndexOf(columns, "CPUStartTime");
        if (appCol < 0 || pidCol < 0 || msCol < 0) return;

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length != columns.Length) continue;
            if (!int.TryParse(fields[pidCol], out int pid)) continue;
            if (!double.TryParse(fields[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) continue;
            if (ms <= 0) continue;

            double now = _clock.Elapsed.TotalSeconds;
            double time = now;
            if (timeCol >= 0 &&
                double.TryParse(fields[timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double traceTime))
            {
                _clockOffset = Math.Min(_clockOffset, now - traceTime);
                time = traceTime + _clockOffset;
            }

            lock (_lock)
            {
                if (!_processes.TryGetValue(pid, out var frames))
                    _processes[pid] = frames = new ProcessFrames { Application = fields[appCol] };
                frames.Frames.Enqueue((time, ms));
            }
        }
    }

    /// <summary>
    /// Statistics for a process. Falls back to the busiest process with the same executable name,
    /// which covers applications that present from a helper process.
    /// </summary>
    public FrameStats? GetStats(int processId, string processName)
    {
        double now = _clock.Elapsed.TotalSeconds;
        lock (_lock)
        {
            foreach (var (pid, frames) in _processes.ToArray())
            {
                while (frames.Frames.Count > 0 && frames.Frames.Peek().Time < now - WindowSeconds)
                    frames.Frames.Dequeue();
                if (frames.Frames.Count == 0) _processes.Remove(pid);
            }

            if (!_processes.TryGetValue(processId, out var target))
            {
                var exe = processName + ".exe";
                target = _processes.Values
                    .Where(p => string.Equals(p.Application, exe, StringComparison.OrdinalIgnoreCase))
                    .MaxBy(p => p.Frames.Count);
            }
            if (target is null) return null;

            int lastSecond = 0;
            var times = new double[target.Frames.Count];
            int i = 0;
            foreach (var (time, ms) in target.Frames)
            {
                times[i++] = ms;
                if (time >= now - 1) lastSecond++;
            }
            if (lastSecond == 0) return null;

            Array.Sort(times);
            return new FrameStats(
                lastSecond,
                Percentile(times, 0.50),
                Percentile(times, 0.95),
                Percentile(times, 0.99),
                times.Length);
        }
    }

    static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int low = (int)rank;
        int high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    public void Dispose()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}
