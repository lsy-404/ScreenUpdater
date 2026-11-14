using System.Text.Json;

namespace ScreenUpdater.Metrics;

public sealed record WeatherReport(string Condition, double Temperature, double Low, double High);

/// <summary>Current conditions and today's range from Open-Meteo, refreshed in the background.</summary>
public sealed class Weather : IDisposable
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly CancellationTokenSource _stop = new();
    volatile WeatherReport? _latest;

    public WeatherReport? Latest => _latest;

    public Weather(double latitude, double longitude, Action<string> log)
    {
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.##}&longitude={longitude:0.##}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var json = JsonDocument.Parse(await _http.GetStringAsync(url, _stop.Token));
                    var current = json.RootElement.GetProperty("current");
                    var daily = json.RootElement.GetProperty("daily");
                    _latest = new WeatherReport(
                        Describe(current.GetProperty("weather_code").GetInt32()),
                        current.GetProperty("temperature_2m").GetDouble(),
                        daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                        daily.GetProperty("temperature_2m_max")[0].GetDouble());
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException
                                              or InvalidOperationException or TaskCanceledException)
                {
                    if (!_stop.IsCancellationRequested) log($"weather: {e.Message}");
                }

                try
                {
                    await Task.Delay(RefreshInterval, _stop.Token);
                }
                catch (TaskCanceledException)
                {
                }
            }
        });
    }

    /// <summary>WMO weather interpretation codes.</summary>
    static string Describe(int code) => code switch
    {
        0 => "晴",
        1 => "少云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 or 81 => "阵雨",
        82 => "强阵雨",
        85 or 86 => "阵雪",
        95 or 97 => "雷暴",
        96 or 99 => "雷暴冰雹",
        _ => "",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
