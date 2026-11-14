using System.Text.Json;

namespace ScreenUpdater;

public sealed class Config
{
    /// <summary>COM port; detected from the screen's USB identity when empty.</summary>
    public string? Port { get; set; }

    /// <summary>Rotate the landscape image by 180°.</summary>
    public bool Flip { get; set; } = true;

    /// <summary>0 (darkest) to 100 (brightest).</summary>
    public int Brightness { get; set; } = 40;

    public int IntervalMs { get; set; } = 1000;

    /// <summary>PresentMon console executable; searched in tools\ and the winget packages when empty.</summary>
    public string? PresentMonPath { get; set; }

    /// <summary>Location for the weather in the title bar; no weather when unset.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public static Config Load(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) return new Config();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options) ?? new Config();
    }
}
