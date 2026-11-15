using System.Diagnostics;
using System.Globalization;

namespace ScreenUpdater.Metrics;

/// <param name="Fps">Presents during the last second; 0 while the application is not drawing.</param>
/// <param name="P50Ms">Median frame time over the window.</param>
public readonly record struct FrameStats(double Fps, double P50Ms, double P95Ms, double P99Ms, int Samples);

/// <summary>Streams per-frame data from the PresentMon console application.</summary>
public sealed class FrameMonitor : IDisposable
{
    const double WindowSeconds = 10;

    sealed class ProcessFrames
    {
        public required string Application;
        public readonly Queue<(double Time, double Ms)> Frames = new();
    }

    readonly object _lock = new();
    // One entry per swap chain: a process with several windows presents on each independently.
    readonly Dictionary<(int ProcessId, string SwapChain), ProcessFrames> _processes = [];
    readonly HashSet<string> _presented = new(StringComparer.OrdinalIgnoreCase);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly ChildProcessJob _job = new();
    readonly Action<string> _log;
    readonly string? _exe;
    readonly string _sessionName;
    volatile Process? _process;
    volatile bool _disposed;

    // Trace timestamp of the newest row and when it arrived, to extrapolate the current trace time.
    double _latestTrace;
    double _latestArrival;

    public bool Available => _process is { HasExited: false };

    public FrameMonitor(string? configuredPath, string baseDirectory, string sessionName, Action<string> log)
    {
        _log = log;
        _sessionName = sessionName;
        _exe = Locate(configuredPath, baseDirectory);
        if (_exe is null)
        {
            log("PresentMon not found; frame statistics disabled");
            return;
        }
        new Thread(Supervise) { IsBackground = true, Name = "PresentMon" }.Start();
    }

    /// <summary>Keeps PresentMon running, restarting it when it exits.</summary>
    void Supervise()
    {
        while (!_disposed)
        {
            var info = new ProcessStartInfo(_exe!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
                     {
                         "--output_stdout", "--no_console_stats", "--stop_existing_session",
                         "--session_name", _sessionName,
                     })
                info.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(info)!;
                _job.Add(process);
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log($"PresentMon: {e.Data}"); };
                process.BeginErrorReadLine();
                _process = process;
                _log($"PresentMon started: {_exe}");

                lock (_lock) _processes.Clear();
                ReadLoop(process.StandardOutput);
                process.WaitForExit();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _log($"PresentMon failed: {e.Message}");
            }
            finally
            {
                _process = null;
            }

            if (!_disposed) Thread.Sleep(5000);
        }
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

    void ReadLoop(StreamReader reader)
    {
        var header = reader.ReadLine();
        if (header is null) return;

        var columns = header.Split(',');
        int appCol = Array.IndexOf(columns, "Application");
        int pidCol = Array.IndexOf(columns, "ProcessID");
        int chainCol = Array.IndexOf(columns, "SwapChainAddress");
        int msCol = Array.IndexOf(columns, "MsBetweenPresents");
        int timeCol = Array.IndexOf(columns, "CPUStartTime");
        if (appCol < 0 || pidCol < 0 || msCol < 0)
        {
            _log($"PresentMon: unexpected header: {header}");
            return;
        }
        bool first = true;

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length != columns.Length) continue;
            if (!int.TryParse(fields[pidCol], out int pid)) continue;
            if (!double.TryParse(fields[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) continue;
            if (ms <= 0) continue;
            if (first)
            {
                first = false;
                _log("PresentMon: receiving frames");
            }

            double arrival = _clock.Elapsed.TotalSeconds;
            double time = timeCol >= 0 &&
                          double.TryParse(fields[timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double traceTime)
                ? traceTime
                : arrival;

            lock (_lock)
            {
                _latestTrace = time;
                _latestArrival = arrival;
                var key = (pid, chainCol >= 0 ? fields[chainCol] : "");
                if (!_processes.TryGetValue(key, out var frames))
                {
                    _processes[key] = frames = new ProcessFrames { Application = fields[appCol] };
                    _presented.Add(fields[appCol]);
                }
                frames.Frames.Enqueue((time, ms));
            }
        }
    }
    /// <summary>Whether the executable has presented at any point; an idle window presents nothing.</summary>
    public bool HasPresented(string processName)
    {
        lock (_lock) return _presented.Contains(processName + ".exe");
    }

    /// <summary>
    /// Statistics for the busiest swap chain of a process. Falls back to the busiest one of any process
    /// with the same executable name, which covers applications that present from a helper process.
    /// </summary>
    public FrameStats? GetStats(int processId, string processName)
    {
        lock (_lock)
        {
            double now = _latestTrace + (_clock.Elapsed.TotalSeconds - _latestArrival);
            foreach (var (key, frames) in _processes.ToArray())
            {
                while (frames.Frames.Count > 0 && frames.Frames.Peek().Time < now - WindowSeconds)
                    frames.Frames.Dequeue();
                if (frames.Frames.Count == 0) _processes.Remove(key);
            }

            var exe = processName + ".exe";
            var target = _processes.Where(p => p.Key.ProcessId == processId).Select(p => p.Value).MaxBy(p => p.Frames.Count)
                ?? _processes.Values
                    .Where(p => string.Equals(p.Application, exe, StringComparison.OrdinalIgnoreCase))
                    .MaxBy(p => p.Frames.Count);
            if (target is null) return null;

            int lastSecond = 0;
            var times = new double[target.Frames.Count];
            int i = 0;
            foreach (var (time, ms) in target.Frames)
            {
                times[i++] = ms;
                if (time >= now - 1) lastSecond++;
            }

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
        _disposed = true;
        _job.Dispose();
    }
}