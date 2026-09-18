namespace Lyrider.Models;

public sealed class AppSettings
{
    public const string DefaultApiBaseUrl = "http://localhost:10767/";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public string Theme { get; set; } = "Dark";

    public double LyricFontSize { get; set; } = 42;

    public bool AutoScrollLyrics { get; set; } = true;

    public bool ConvertTraditionalLyricsToSimplified { get; set; }

    public bool AlwaysOnTop { get; set; }

    public bool TaskbarWidgetEnabled { get; set; }

    public bool ShowLyricsInTaskbar { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; }

    public string DefaultPanel { get; set; } = "Queue";

    /// <summary>Backdrop artwork opacity, as a 0–1 fraction.</summary>
    public double BackgroundOpacity { get; set; } = 0.14;

    /// <summary>Backdrop artwork blur radius, as a 0–100 percentage.</summary>
    public double BackgroundBlur { get; set; } = 40;
}
