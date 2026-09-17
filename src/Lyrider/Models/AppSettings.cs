namespace Lyrider.Models;

public sealed class AppSettings
{
    public const string DefaultApiBaseUrl = "http://localhost:10767/";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public string Theme { get; set; } = "Dark";

    public double LyricFontSize { get; set; } = 42;

    public bool AutoScrollLyrics { get; set; } = true;

    public bool AlwaysOnTop { get; set; }

    public bool ShowVolume { get; set; } = true;

    public string DefaultPanel { get; set; } = "Queue";

    public double BackgroundOpacity { get; set; } = 0.14;
}
