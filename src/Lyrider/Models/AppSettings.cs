namespace Lyrider.Models;

public sealed class AppSettings
{
    public const string DefaultApiBaseUrl = "http://localhost:10767/";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public string Theme { get; set; } = "Dark";

    public string Language { get; set; } = "System";

    public double LyricFontSize { get; set; } = 42;

    public bool ConvertTraditionalLyricsToSimplified { get; set; }

    public string LyricsSource { get; set; } = nameof(global::Lyrider.Models.LyricsSource.Auto);

    public bool ShowLyricsTranslation { get; set; }

    public bool TaskbarWidgetEnabled { get; set; }

    public bool ShowLyricsInTaskbar { get; set; } = true;

    /// <summary>
    /// Experimental: mirror the taskbar widget when the Windows taskbar aligns its icons to the
    /// left, so the lyrics sit right-aligned to the left of the artwork, which moves right.
    /// </summary>
    public bool RightAlignTaskbarLyrics { get; set; }

    public bool StartSilently { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    public bool HasCompletedOnboarding { get; set; }

    /// <summary>Backdrop artwork opacity, as a 0–1 fraction.</summary>
    public double BackgroundOpacity { get; set; } = 0.14;

    /// <summary>Backdrop artwork blur radius, as a 0–100 percentage.</summary>
    public double BackgroundBlur { get; set; } = 40;
}
