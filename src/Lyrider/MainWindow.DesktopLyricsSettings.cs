using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Lyrider;

public sealed partial class MainWindow
{
    private bool _loadingDesktopLyricsControls = true;
    private bool _changingDesktopPreset;
    private long _desktopPreviewVersion;
    private string _customPlayedColor = "#4FDFFF";
    private string _customUnplayedColor = "#FFFFFF";

    private static readonly (string Tag, string Played, string Unplayed)[] DesktopColorPresets =
    [
        ("Blue", "#4FDFFF", "#FFFFFF"),
        ("Gold", "#FFD166", "#FFF4DC"),
        ("Purple", "#A78BFA", "#EDE9FE"),
        ("Mint", "#6EE7B7", "#ECFDF5")
    ];

    private static string RadioTag(RadioButtons buttons, string fallback) =>
        (buttons.SelectedItem as FrameworkElement)?.Tag as string ?? fallback;

    private static void SelectRadioTag(RadioButtons buttons, string tag) =>
        buttons.SelectedItem = buttons.Items.OfType<FrameworkElement>().FirstOrDefault(item => Equals(item.Tag, tag));

    private DesktopLyricsOptions ReadDesktopLyricsDraft() => (_settings.DesktopLyrics with
    {
        Enabled = DesktopLyricsEnabledToggle.IsOn,
        Locked = DesktopLyricsLockedToggle.IsOn,
        HideWhenPaused = DesktopLyricsPauseToggle.IsOn,
        DoubleLineEnabled = RadioTag(DesktopLyricsLineRadioButtons, "Double") == "Double",
        TranslationEnabled = DesktopLyricsTranslationToggle.IsOn,
        TextDirection = Enum.Parse<DesktopLyricsTextDirection>(RadioTag(DesktopLyricsDirectionRadioButtons, "Horizontal")),
        Alignment = Enum.Parse<DesktopLyricsAlignment>(RadioTag(DesktopLyricsAlignmentRadioButtons, "Center")),
        FontSize = DesktopLyricsFontSlider.Value,
        FontWeight = Enum.Parse<DesktopLyricsFontWeight>(SelectedTag(DesktopLyricsFontWeightComboBox, "SemiBold")),
        StrokeThickness = DesktopLyricsStrokeSlider.Value,
        StrokeColor = ColorHex(DesktopLyricsStrokeColorPicker.Color),
        TextColor = ColorHex(DesktopLyricsColorPicker.Color),
        HighlightColor = ColorHex(DesktopLyricsHighlightColorPicker.Color),
        BackgroundOpacity = DesktopLyricsBackgroundSlider.Value / 100,
        KaraokeEnabled = DesktopLyricsKaraokeToggle.IsOn
    }).Normalize();

    private static Windows.UI.Color ParseDesktopColor(string hex)
    {
        var rgb = Convert.ToUInt32(hex[1..], 16);
        return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void LoadDesktopLyricsControls()
    {
        _loadingDesktopLyricsControls = true;
        try
        {
            var options = _settings.DesktopLyrics;
            DesktopLyricsEnabledToggle.IsOn = options.Enabled;
            DesktopLyricsLockedToggle.IsOn = options.Locked;
            DesktopLyricsPauseToggle.IsOn = options.HideWhenPaused;
            DesktopLyricsTranslationToggle.IsOn = options.ShowTranslation;
            DesktopLyricsKaraokeToggle.IsOn = options.KaraokeEnabled;
            SelectRadioTag(DesktopLyricsLineRadioButtons, options.ShowDoubleLine ? "Double" : "Single");
            SelectRadioTag(DesktopLyricsDirectionRadioButtons, options.TextDirection.ToString());
            SelectRadioTag(DesktopLyricsAlignmentRadioButtons, options.EffectiveAlignment.ToString());
            SelectByTag(DesktopLyricsFontWeightComboBox, options.FontWeight.ToString());
            DesktopLyricsFontSlider.Value = DesktopLyricsFontNumberBox.Value = options.FontSize;
            DesktopLyricsStrokeSlider.Value = DesktopLyricsStrokeNumberBox.Value = options.StrokeThickness;
            DesktopLyricsBackgroundSlider.Value = options.BackgroundOpacity * 100;
            DesktopLyricsBackgroundValueText.Text = FormatPercent(DesktopLyricsBackgroundSlider.Value);
            DesktopLyricsHighlightColorPicker.Color = ParseDesktopColor(options.HighlightColor);
            DesktopLyricsColorPicker.Color = ParseDesktopColor(options.TextColor);
            DesktopLyricsStrokeColorPicker.Color = ParseDesktopColor(options.StrokeColor);
            DesktopLyricsHighlightColorPreview.Background = new SolidColorBrush(DesktopLyricsHighlightColorPicker.Color);
            DesktopLyricsColorPreview.Background = new SolidColorBrush(DesktopLyricsColorPicker.Color);
            DesktopLyricsStrokeColorPreview.Background = new SolidColorBrush(DesktopLyricsStrokeColorPicker.Color);
            _customPlayedColor = options.HighlightColor;
            _customUnplayedColor = options.TextColor;
            SelectMatchingDesktopPreset();
        }
        finally { _loadingDesktopLyricsControls = false; }
        RefreshDesktopLyricsPreview();
    }

    private void UpdateDesktopLyricsSettingsLayout()
    {
        if (DesktopLyricsAlignmentRadioButtons is null) return;
        var compact = RootGrid.ActualWidth < 720;
        DesktopLyricsAlignmentRadioButtons.MaxColumns = compact ? 2 : 4;
        DesktopLyricsPresetRadioButtons.MaxColumns = compact ? 2 : 5;
        DesktopLyricsCustomColorsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DesktopLyricsUnplayedColorHost, compact ? 0 : 1);
        Grid.SetRow(DesktopLyricsUnplayedColorHost, compact ? 1 : 0);
    }

    private void SyncDesktopLyricsQuickSettings(DesktopLyricsOptions previous)
    {
        var draft = ReadDesktopLyricsDraft();
        var options = _settings.DesktopLyrics;
        var wasLoading = _loadingDesktopLyricsControls;
        _loadingDesktopLyricsControls = true;
        try
        {
            if (draft.Enabled == previous.Enabled) DesktopLyricsEnabledToggle.IsOn = options.Enabled;
            if (draft.Locked == previous.Locked) DesktopLyricsLockedToggle.IsOn = options.Locked;
            if (draft.FontSize == previous.FontSize)
                DesktopLyricsFontSlider.Value = DesktopLyricsFontNumberBox.Value = options.FontSize;
            if (draft.ShowDoubleLine == previous.ShowDoubleLine)
                SelectRadioTag(DesktopLyricsLineRadioButtons, options.ShowDoubleLine ? "Double" : "Single");
            if (draft.EffectiveAlignment == previous.EffectiveAlignment)
                SelectRadioTag(DesktopLyricsAlignmentRadioButtons, options.EffectiveAlignment.ToString());
            if (draft.TextDirection == previous.TextDirection)
                SelectRadioTag(DesktopLyricsDirectionRadioButtons, options.TextDirection.ToString());
            if (draft.ShowTranslation == previous.ShowTranslation) DesktopLyricsTranslationToggle.IsOn = options.ShowTranslation;
            if (draft.KaraokeEnabled == previous.KaraokeEnabled) DesktopLyricsKaraokeToggle.IsOn = options.KaraokeEnabled;
        }
        finally { _loadingDesktopLyricsControls = wasLoading; }
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsSettingChanged(object sender, RoutedEventArgs e) => RefreshDesktopLyricsPreview();

    private void SelectMatchingDesktopPreset()
    {
        var played = ColorHex(DesktopLyricsHighlightColorPicker.Color);
        var unplayed = ColorHex(DesktopLyricsColorPicker.Color);
        var preset = DesktopColorPresets.FirstOrDefault(item => item.Played == played && item.Unplayed == unplayed);
        _changingDesktopPreset = true;
        try { SelectRadioTag(DesktopLyricsPresetRadioButtons, preset.Tag ?? "Custom"); }
        finally { _changingDesktopPreset = false; }
    }

    private void DesktopLyricsPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDesktopLyricsControls || _changingDesktopPreset) return;
        var tag = RadioTag(DesktopLyricsPresetRadioButtons, "Custom");
        var preset = DesktopColorPresets.FirstOrDefault(item => item.Tag == tag);
        _changingDesktopPreset = true;
        try
        {
            DesktopLyricsHighlightColorPicker.Color = ParseDesktopColor(preset.Played ?? _customPlayedColor);
            DesktopLyricsColorPicker.Color = ParseDesktopColor(preset.Unplayed ?? _customUnplayedColor);
        }
        finally { _changingDesktopPreset = false; }
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsHighlightColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (DesktopLyricsHighlightColorPreview is not null)
            DesktopLyricsHighlightColorPreview.Background = new SolidColorBrush(args.NewColor);
        DesktopLyricsCustomColorChanged();
    }

    private void DesktopLyricsColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (DesktopLyricsColorPreview is not null)
            DesktopLyricsColorPreview.Background = new SolidColorBrush(args.NewColor);
        DesktopLyricsCustomColorChanged();
    }

    private void DesktopLyricsCustomColorChanged()
    {
        if (_loadingDesktopLyricsControls || _changingDesktopPreset) return;
        _customPlayedColor = ColorHex(DesktopLyricsHighlightColorPicker.Color);
        _customUnplayedColor = ColorHex(DesktopLyricsColorPicker.Color);
        _changingDesktopPreset = true;
        try { SelectRadioTag(DesktopLyricsPresetRadioButtons, "Custom"); }
        finally { _changingDesktopPreset = false; }
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsStrokeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (DesktopLyricsStrokeColorPreview is not null)
            DesktopLyricsStrokeColorPreview.Background = new SolidColorBrush(args.NewColor);
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsFontSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DesktopLyricsFontNumberBox is not null) DesktopLyricsFontNumberBox.Value = e.NewValue;
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsFontNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (DesktopLyricsFontSlider is not null && double.IsFinite(args.NewValue))
            DesktopLyricsFontSlider.Value = Math.Clamp(args.NewValue, 16, 72);
    }

    private void DesktopLyricsStrokeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DesktopLyricsStrokeNumberBox is not null) DesktopLyricsStrokeNumberBox.Value = e.NewValue;
        RefreshDesktopLyricsPreview();
    }

    private void DesktopLyricsStrokeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (DesktopLyricsStrokeSlider is not null && double.IsFinite(args.NewValue))
            DesktopLyricsStrokeSlider.Value = Math.Clamp(args.NewValue, 0, 8);
    }

    private void DesktopLyricsBackgroundSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DesktopLyricsBackgroundValueText is not null) DesktopLyricsBackgroundValueText.Text = FormatPercent(e.NewValue);
        RefreshDesktopLyricsPreview();
    }

    private async void RefreshDesktopLyricsPreview()
    {
        if (_loadingDesktopLyricsControls || _changingDesktopPreset || DesktopLyricsPreviewImage is null) return;
        var version = ++_desktopPreviewVersion;
        try
        {
            await Task.Delay(100, _lifetimeCancellation.Token);
            if (version != _desktopPreviewVersion) return;
            var translated = SelectedTag(DesktopLyricsPreviewSceneComboBox, "Lyrics") == "Translation";
            var state = new DesktopLyricsState("歌词示例", true, false,
                translated ? "Stay with me 2026" : "让旋律陪你走过每一天",
                translated ? "让旋律陪你走过每一天" : null,
                translated ? "Until the morning comes" : "下一句也有自己的颜色",
                new DesktopLyricsTiming([new(0, 10, 0, 1)], 3.5, 0), IsChineseSong: !translated);
            var bytes = await _desktopLyricsHost.RenderPreviewAsync(ReadDesktopLyricsDraft(), state, 0.35);
            if (version != _desktopPreviewVersion || _lifetimeCancellation.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            await stream.WriteAsync(writer.DetachBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (version != _desktopPreviewVersion || _lifetimeCancellation.IsCancellationRequested) return;
            DesktopLyricsPreviewImage.Source = bitmap;
            DesktopLyricsPreviewStatusText.Text = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (version == _desktopPreviewVersion && !_lifetimeCancellation.IsCancellationRequested)
                DesktopLyricsPreviewStatusText.Text = AppText.Get("预览暂时无法显示，请重新调整设置。", "Preview is unavailable. Adjust a setting to try again.");
        }
    }
}
