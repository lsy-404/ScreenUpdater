using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenUpdater.Lcd;

namespace ScreenUpdater;

public sealed class Config
{
    /// <summary>COM port; detected from the screen's USB identity when empty.</summary>
    public string? Port { get; set; }

    public Orientation Orientation { get; set; } = Orientation.ReverseLandscape;

    /// <summary>0 (darkest) to 100 (brightest).</summary>
    public int Brightness { get; set; } = 40;

    public int IntervalMs { get; set; } = 1000;

    /// <summary>PresentMon console executable; searched in tools\ and the winget links when empty.</summary>
    public string? PresentMonPath { get; set; }

    public static Config Load(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) return new Config();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options) ?? new Config();
    }
}
