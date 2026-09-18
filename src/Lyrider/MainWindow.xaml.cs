using System.Collections.ObjectModel;
using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace Lyrider;

public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly TokenStore _tokenStore = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ObservableCollection<QueueItemInfo> _queueItems = [];
    private readonly List<LyricLineVisual> _lyricLines = [];
    private readonly SolidColorBrush _transparentLyricBackground = new(Colors.Transparent);
    private readonly TaskbarWidgetHost _taskbarWidgetHost = new();
    private readonly LyricsService _lyricsService = new();
    private readonly CiderService _ciderService;
    private readonly AppWindow _appWindow;
    private readonly TrayIconHost _trayIconHost;

    private AppSettings _settings;
    private IReadOnlyList<LyricLineInfo> _lyrics = [];
    private CancellationTokenSource? _volumeChangeCancellation;
    private string? _appToken;
    private string? _artworkUrl;
    private string? _currentTrackKey;
    private bool _isRefreshing;
    private bool _isUpdatingVolume;
    private bool _lyricsAreTimeSynced;
    private bool _isInitialized;
    private bool _isRunningTaskbarCommand;
    private bool _isExitRequested;
    private bool _isSettingsTransitioning;
    private int _currentLyricIndex = -1;
    private int _refreshCount;
    private int _renderedLyricsSignature;
    private double _lastPlaybackTime;
    private int _optimisticSeekIndex = -1;
    private int _hoveredLyricIndex = -1;
    private double _optimisticSeekTarget;
    private double? _pendingAutoScrollOffset;
    private DateTimeOffset _lastManualLyricsScroll = DateTimeOffset.MinValue;
    private DateTimeOffset _autoScrollDeadline;
    private DateTimeOffset _optimisticSeekDeadline;

    private const int LyricAnimationMilliseconds = 180;
    private const int AutoScrollSettleMilliseconds = 700;
    private const double OptimisticSeekSeconds = 2.5;
    private const double OptimisticSeekToleranceSeconds = 1.5;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _ciderService = new CiderService(_settings.ApiBaseUrl);

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Lyrider.ico");
        _appWindow.SetIcon(iconPath);
        _trayIconHost = new TrayIconHost(iconPath, ShowFromTray, ExitApplication);
        _appWindow.Closing += AppWindow_Closing;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        var scale = GetDpiForWindow(windowHandle) / 96.0;
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.ResizeClient(new SizeInt32(
            Math.Min((int)(1360 * scale), Math.Max(1, workArea.Width - (int)(32 * scale))),
            Math.Min((int)(820 * scale), Math.Max(1, workArea.Height - (int)(64 * scale)))));

        QueueListView.ItemsSource = _queueItems;
        _taskbarWidgetHost.CommandRequested += TaskbarWidgetHost_CommandRequested;
        LoadSavedToken();
        LoadSettingsControls();
        ApplySettings();
        ShowPanel(_settings.DefaultPanel);

        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
        _isInitialized = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.XamlRoot.Changed += XamlRoot_Changed;
        UpdateWindowMinimumSize();
        UpdateResponsiveLayout();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateWindowMinimumSize();
    }

    private void UpdateWindowMinimumSize()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            var scale = RootGrid.XamlRoot.RasterizationScale;
            // Presenter limits are outer-window pixels; include the non-client frame.
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(480 * scale)
                + _appWindow.Size.Width - _appWindow.ClientSize.Width;
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(600 * scale)
                + _appWindow.Size.Height - _appWindow.ClientSize.Height;
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (PlaybackPanel is null || RootGrid.ActualWidth <= 0)
        {
            return;
        }

        var compact = RootGrid.ActualWidth < 1000;
        var padding = compact ? 16 : 32;
        var availableHeight = Math.Max(0, RootGrid.ActualHeight - 48 - padding * 2);
        PlayerPageGrid.Padding = new Thickness(padding);
        PlayerPageGrid.ColumnSpacing = compact ? 0 : 32;
        PlayerPageGrid.RowSpacing = compact ? 16 : 0;
        PlayerPageGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 5, GridUnitType.Star);
        PlayerPageGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(7, GridUnitType.Star);
        var playerHeight = compact ? Math.Min(360, availableHeight * 0.53) : availableHeight;
        PlayerPageGrid.RowDefinitions[0].Height = compact ? new GridLength(playerHeight) : new GridLength(1, GridUnitType.Star);
        PlayerPageGrid.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DetailsPanel, compact ? 0 : 1);
        Grid.SetRow(DetailsPanel, compact ? 1 : 0);
        PlaybackPanel.RowSpacing = compact ? 6 : 14;
        var playerWidth = compact ? RootGrid.ActualWidth - padding * 2
            : (RootGrid.ActualWidth - padding * 2 - 32) * 5 / 12;
        var artworkSize = Math.Max(0, Math.Min(420, Math.Min(playerWidth,
            playerHeight - (_settings.ShowVolume ? 210 : 170))));
        ArtworkBorder.Width = artworkSize;
        ArtworkBorder.Height = artworkSize;
        ArtworkBorder.Visibility = artworkSize < 48 ? Visibility.Collapsed : Visibility.Visible;
        CurrentQueueSection.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UpcomingQueueHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        QueuePanel.Padding = new Thickness(0, 0, 0, 56);
        LyricsPanel.Padding = new Thickness(14, 0, 0, 56);
        UpdateLyricGutters();
        if (LyricsPanel.Visibility == Visibility.Visible && _currentLyricIndex >= 0)
        {
            ScrollToCurrentLyric(animate: false);
        }
        SettingsPageGrid.Padding = new Thickness(0, compact ? 16 : 24, 0, 32);
        var settingsLayoutWidth = Math.Max(0, Math.Min(1020, RootGrid.ActualWidth - padding * 2));
        SettingsHeaderGrid.Width = settingsLayoutWidth;
        SettingsContentGrid.Width = settingsLayoutWidth;

        foreach (var row in new[] { ThemeSettingsRow, BackgroundSettingsRow, LyricFontSettingsRow,
            AutoScrollSettingsRow, DefaultPanelSettingsRow, VolumeSettingsRow, AlwaysOnTopSettingsRow,
            TaskbarWidgetSettingsRow, MinimizeToTraySettingsRow })
        {
            var stacked = RootGrid.ActualWidth < 720;
            row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 260);
            Grid.SetColumn((FrameworkElement)row.Children[1], stacked ? 0 : 1);
            Grid.SetRow((FrameworkElement)row.Children[1], stacked ? 1 : 0);
        }

        var stackConnectionFields = RootGrid.ActualWidth < 820;
        ConnectionFieldsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ConnectionFieldsGrid.ColumnDefinitions[1].Width = new GridLength(stackConnectionFields ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(TokenPasswordBox, stackConnectionFields ? 0 : 1);
        Grid.SetRow(TokenPasswordBox, stackConnectionFields ? 1 : 0);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await RefreshAsync(forceDetails: true);
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool forceDetails = false)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var nowPlayingTask = _ciderService.GetNowPlayingAsync(
                _appToken,
                _lifetimeCancellation.Token);
            var playbackStatusTask = _ciderService.GetPlaybackStatusAsync(
                _appToken,
                _lifetimeCancellation.Token);

            await Task.WhenAll(nowPlayingTask, playbackStatusTask);
            var result = await nowPlayingTask;
            var playbackStatus = await playbackStatusTask;

            UpdateConnectionState(result);
            UpdatePlaybackStatus(playbackStatus);

            if (result.State != CiderConnectionState.Connected || result.Track is null)
            {
                UpdateTaskbarWidget(null, playbackStatus);
                UpdateNowPlaying(null);
                return;
            }

            var track = result.Track;
            var trackKey = GetTrackKey(track);
            var trackChanged = !string.Equals(trackKey, _currentTrackKey, StringComparison.Ordinal);
            UpdateNowPlaying(track);

            if (trackChanged || forceDetails)
            {
                _currentTrackKey = trackKey;
                if (trackChanged)
                {
                    _lyrics = [];
                    _lyricsAreTimeSynced = false;
                    RenderLyrics();
                    UpdateTaskbarWidget(track, playbackStatus);
                }

                await RefreshTrackDetailsAsync(track);
                UpdateTaskbarWidget(track, playbackStatus);
            }
            else
            {
                UpdateCurrentLyric(track.CurrentPlaybackTime);
                UpdateTaskbarWidget(track, playbackStatus);
                _refreshCount++;
                if (_refreshCount % 5 == 0)
                {
                    await RefreshQueueAsync(track);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshTrackDetailsAsync(NowPlayingInfo track)
    {
        var trackId = track.PlayParameters?.Id;
        var queueTask = _ciderService.GetQueueAsync(
            _appToken,
            trackId,
            _lifetimeCancellation.Token);
        var ciderLyricsTask = _ciderService.GetLyricsAsync(
            trackId,
            _appToken,
            _lifetimeCancellation.Token);

        await Task.WhenAll(queueTask, ciderLyricsTask);
        ApplyQueue(await queueTask);
        var lyrics = await _lyricsService.ResolveAsync(
            track,
            await ciderLyricsTask,
            _lifetimeCancellation.Token);
        _lyrics = lyrics.Lines;
        _lyricsAreTimeSynced = lyrics.IsTimeSynced;
        var rebuilt = RenderLyrics();
        UpdateCurrentLyric(track.CurrentPlaybackTime, forceScroll: rebuilt);
    }

    private async Task RefreshQueueAsync(NowPlayingInfo track)
    {
        var queue = await _ciderService.GetQueueAsync(
            _appToken,
            track.PlayParameters?.Id,
            _lifetimeCancellation.Token);
        ApplyQueue(queue);
    }

    private void UpdateConnectionState(CiderResult result)
    {
        ConnectionStatusMenuItem.Text = result.Message;
        ConnectionStatusIcon.Foreground = result.State == CiderConnectionState.Connected
            ? (Brush)RootGrid.Resources["ConnectedBrush"]
            : (Brush)RootGrid.Resources["DisconnectedBrush"];
    }

    private void UpdatePlaybackStatus(PlaybackStatus? status)
    {
        if (status is null)
        {
            return;
        }

        PlayPauseIcon.Glyph = status.IsPlaying ? "\uE769" : "\uE768";
        _isUpdatingVolume = true;
        VolumeSlider.Value = status.Volume;
        _isUpdatingVolume = false;
    }

    private void UpdateNowPlaying(NowPlayingInfo? track)
    {
        if (track is null)
        {
            _currentTrackKey = null;
            SongNameText.Text = "未在播放";
            ArtistAlbumText.Text = "—";
            CurrentQueueSongText.Text = "未在播放";
            CurrentQueueArtistText.Text = "—";
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = "−0:00";
            PlaybackProgressBar.Maximum = 1;
            PlaybackProgressBar.Value = 0;
            SetArtwork(null);
            ApplyQueue(new QueueSnapshot([], -1));
            _lyrics = [];
            _lastPlaybackTime = 0;
            RenderLyrics();
            return;
        }

        var durationSeconds = Math.Max(0, track.DurationInMillis / 1000);
        var currentSeconds = Math.Clamp(track.CurrentPlaybackTime, 0, durationSeconds);
        _lastPlaybackTime = currentSeconds;
        var songName = ValueOrFallback(track.Name);
        var artistName = ValueOrFallback(track.ArtistName);
        var albumName = ValueOrFallback(track.AlbumName);

        SongNameText.Text = songName;
        ArtistAlbumText.Text = $"{albumName} — {artistName}";
        CurrentQueueSongText.Text = songName;
        CurrentQueueArtistText.Text = $"{artistName} — {albumName}";
        CurrentTimeText.Text = FormatTime(currentSeconds);
        RemainingTimeText.Text = $"−{FormatTime(Math.Max(0, durationSeconds - currentSeconds))}";
        PlaybackProgressBar.Maximum = Math.Max(1, durationSeconds);
        PlaybackProgressBar.Value = currentSeconds;
        ShuffleButton.Opacity = track.ShuffleMode > 0 ? 1 : 0.55;
        RepeatButton.Opacity = track.RepeatMode > 0 ? 1 : 0.55;
        SetArtwork(NormalizeArtworkUrl(track.Artwork?.Url));
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarTheme();
        ApplyLyricTheme();
        _trayIconHost.SetLightTheme(RootGrid.ActualTheme == ElementTheme.Light);
    }

    private void ApplyTitleBarTheme()
    {
        var titleBar = _appWindow.TitleBar;
        var isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var foreground = isLight ? Colors.Black : Colors.White;

        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(
            0x66,
            foreground.R,
            foreground.G,
            foreground.B);
        titleBar.ButtonHoverBackgroundColor = isLight
            ? ColorHelper.FromArgb(0x0F, 0, 0, 0)
            : ColorHelper.FromArgb(0x18, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = isLight
            ? ColorHelper.FromArgb(0x18, 0, 0, 0)
            : ColorHelper.FromArgb(0x24, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private void UpdateTaskbarWidget(NowPlayingInfo? track, PlaybackStatus? status)
    {
        if (track is null)
        {
            _taskbarWidgetHost.Update(TaskbarPlaybackState.Unavailable);
            return;
        }

        _taskbarWidgetHost.Update(new TaskbarPlaybackState(
            ValueOrFallback(track.Name),
            ValueOrFallback(track.ArtistName),
            NormalizeArtworkUrl(track.Artwork?.Url, 160),
            status?.IsPlaying ?? false,
            true,
            _lyricsAreTimeSynced && _currentLyricIndex >= 0
                ? _lyrics[_currentLyricIndex].Text
                : null,
            _lyricsAreTimeSynced && _currentLyricIndex >= 0 && _currentLyricIndex + 1 < _lyrics.Count
                ? _lyrics[_currentLyricIndex + 1].Text
                : null));
    }

    private void SetArtwork(string? url)
    {
        if (string.Equals(_artworkUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _artworkUrl = url;
        var source = Uri.TryCreate(url, UriKind.Absolute, out var artworkUri)
            ? new BitmapImage(artworkUri)
            : null;
        ArtworkImage.Source = source;
        CurrentQueueArtworkImage.Source = source;
        BackgroundArtworkImage.Source = source;
    }

    private void ApplyQueue(QueueSnapshot snapshot)
    {
        var upcoming = snapshot.CurrentIndex >= 0
            ? snapshot.Items.Where(item => item.Index > snapshot.CurrentIndex)
            : snapshot.Items.Where(item => !string.Equals(item.Id, _currentTrackKey, StringComparison.Ordinal));
        var desiredItems = upcoming
            .Select(item => new QueueItemInfo(
                item.Index,
                item.Id,
                item.Name,
                item.ArtistName,
                item.AlbumName,
                item.DurationInMillis,
                NormalizeArtworkUrl(item.ArtworkUrl, 160)))
            .ToArray();
        QueueCollectionSynchronizer.Synchronize(_queueItems, desiredItems);

        QueueCountText.Text = $"{_queueItems.Count} 首";
        EmptyQueueText.Visibility = _queueItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueListView.Visibility = _queueItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Rebuilds the lyric list. Returns false when the content is unchanged, so callers
    /// can skip the follow-up scroll; a needless rebuild would also discard the highlight
    /// and restart any in-flight line animation.
    /// </summary>
    private bool RenderLyrics()
    {
        var signature = LyricPresentation.ComputeLyricsSignature(
            _lyrics,
            _settings.LyricFontSize,
            _lyricsAreTimeSynced);
        if (signature == _renderedLyricsSignature)
        {
            return false;
        }

        _renderedLyricsSignature = signature;
        _optimisticSeekIndex = -1;
        _hoveredLyricIndex = -1;
        LyricsStackPanel.Children.Clear();
        _lyricLines.Clear();
        _currentLyricIndex = -1;
        LyricsEmptyText.Visibility = _lyrics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LyricsStackPanel.Children.Add(LyricsTopSpacer);
        var foreground = CreateLyricForegroundBrush();
        var seekable = _lyricsAreTimeSynced;
        foreach (var line in _lyrics)
        {
            var index = _lyricLines.Count;
            var block = new TextBlock
            {
                Text = line.Text,
                FontSize = _settings.LyricFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var state = StateForLine(index);
            var scale = new ScaleTransform { ScaleX = state.Scale, ScaleY = state.Scale };
            FrameworkElement root = block;
            if (seekable)
            {
                var button = new Button
                {
                    Style = (Style)RootGrid.Resources["LyricLineButtonStyle"],
                    Content = block,
                    Tag = index
                };

                // The template's hover/press states paint a pill behind the text; pointer
                // feedback is a brighter line instead, so keep those fills transparent.
                button.Resources["ButtonBackgroundPointerOver"] = _transparentLyricBackground;
                button.Resources["ButtonBackgroundPressed"] = _transparentLyricBackground;
                button.PointerEntered += LyricLine_PointerEntered;
                button.PointerExited += LyricLine_PointerExited;
                button.Click += LyricLine_Click;
                ToolTipService.SetToolTip(button, $"跳转到 {FormatTime(line.StartTime)}");
                AutomationProperties.SetName(button, line.Text);
                AutomationProperties.SetHelpText(button, $"跳转到 {FormatTime(line.StartTime)}");
                root = button;
            }

            root.Opacity = state.Opacity;
            root.RenderTransform = scale;
            root.RenderTransformOrigin = new Point(0, 0.5);
            _lyricLines.Add(new LyricLineVisual(root, block, scale, state));
            LyricsStackPanel.Children.Add(root);
        }

        LyricsStackPanel.Children.Add(LyricsBottomSpacer);
        UpdateLyricGutters();
        return true;
    }

    private void UpdateCurrentLyric(double playbackTime, bool forceScroll = false)
    {
        if (!_lyricsAreTimeSynced || _lyrics.Count == 0 || _lyricLines.Count == 0)
        {
            return;
        }

        if (!ShouldApplyServerPosition(playbackTime))
        {
            return;
        }

        var nextIndex = LyricPresentation.FindActiveLineIndex(_lyrics, playbackTime);
        if (nextIndex < 0 || (nextIndex == _currentLyricIndex && !forceScroll))
        {
            return;
        }

        SetCurrentLyricIndex(nextIndex, forceScroll);
    }

    /// <summary>
    /// A seek takes a moment to be reflected in the polled playback position, so a fresh
    /// click would be undone by the next tick. Hold the optimistic highlight until the
    /// server catches up to the seek target, or until the pin expires.
    /// </summary>
    private bool ShouldApplyServerPosition(double playbackTime)
    {
        if (_optimisticSeekIndex < 0)
        {
            return true;
        }

        if (Math.Abs(playbackTime - _optimisticSeekTarget) <= OptimisticSeekToleranceSeconds ||
            DateTimeOffset.Now > _optimisticSeekDeadline)
        {
            _optimisticSeekIndex = -1;
            return true;
        }

        return false;
    }

    private void SetCurrentLyricIndex(int index, bool forceScroll)
    {
        _currentLyricIndex = index;
        for (var lineIndex = 0; lineIndex < _lyricLines.Count; lineIndex++)
        {
            var line = _lyricLines[lineIndex];
            var state = StateForLine(lineIndex);
            if (state != line.State)
            {
                ApplyLineState(line, state, animate: true);
            }
        }

        if (_settings.AutoScrollLyrics &&
            (forceScroll || DateTimeOffset.Now - _lastManualLyricsScroll > TimeSpan.FromSeconds(4)))
        {
            ScrollToCurrentLyric(animate: true);
        }
    }

    private static void ApplyLineState(LyricLineVisual line, LyricLineState state, bool animate)
    {
        var previous = line.State;
        line.State = state;
        line.Root.Opacity = state.Opacity;
        line.Scale.ScaleX = state.Scale;
        line.Scale.ScaleY = state.Scale;
        if (!animate || previous == state)
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(LyricAnimationMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateLineAnimation(line.Root, "Opacity", previous.Opacity, state.Opacity, duration, easing));
        storyboard.Children.Add(CreateLineAnimation(line.Scale, "ScaleX", previous.Scale, state.Scale, duration, easing));
        storyboard.Children.Add(CreateLineAnimation(line.Scale, "ScaleY", previous.Scale, state.Scale, duration, easing));

        // FillBehavior.Stop releases the properties when the animation ends, so the local
        // values assigned above stay authoritative. An unreferenced running storyboard can
        // be collected mid-flight, so hold it until it completes.
        line.Storyboard = storyboard;
        storyboard.Completed += (_, _) => line.Storyboard = null;
        storyboard.Begin();
    }

    private static DoubleAnimation CreateLineAnimation(
        DependencyObject target,
        string property,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private void ScrollToCurrentLyric(bool animate)
    {
        if (_currentLyricIndex < 0 ||
            _currentLyricIndex >= _lyricLines.Count ||
            LyricsScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var line = _lyricLines[_currentLyricIndex];
        // Measured against the stack panel, i.e. in content coordinates. Measuring against
        // the scroll viewer does not subtract its scroll offset in WinUI, which would make
        // the target run away towards the end of the lyrics.
        var lineTop = line.Root
            .TransformToVisual(LyricsStackPanel)
            .TransformPoint(new Point(0, 0))
            .Y;
        var target = LyricPresentation.ComputeScrollOffset(
            lineTop,
            line.Root.ActualHeight,
            line.State.Scale,
            LyricsScrollViewer.ViewportHeight,
            LyricsScrollViewer.ExtentHeight);

        // ChangeView animates asynchronously, so its ViewChanged events arrive after this
        // method returns. Record the target so those events are not mistaken for the user
        // scrolling, which would suppress auto-scroll for four seconds.
        _pendingAutoScrollOffset = target;
        _autoScrollDeadline = DateTimeOffset.Now.AddMilliseconds(AutoScrollSettleMilliseconds);
        if (!LyricsScrollViewer.ChangeView(null, target, null, !animate))
        {
            _pendingAutoScrollOffset = null;
        }
    }

    /// <summary>
    /// The spacers let the first and last line reach the anchor by giving every line
    /// something to scroll past, so they have to follow the real viewport height rather
    /// than a fixed value.
    /// </summary>
    private void UpdateLyricGutters()
    {
        var viewportHeight = LyricsScrollViewer.ViewportHeight > 0
            ? LyricsScrollViewer.ViewportHeight
            : LyricsScrollViewer.ActualHeight;
        LyricsTopSpacer.Height = LyricPresentation.TopGutter(viewportHeight);
        LyricsBottomSpacer.Height = LyricPresentation.BottomGutter(viewportHeight);
    }

    private void LyricsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLyricGutters();
        if (LyricsPanel.Visibility == Visibility.Visible && _currentLyricIndex >= 0)
        {
            ScrollToCurrentLyric(animate: false);
        }
    }

    private SolidColorBrush CreateLyricForegroundBrush() =>
        new(RootGrid.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);

    /// <summary>
    /// The lyric panel builds its brushes from <see cref="RootGrid"/>'s resolved theme, so a
    /// theme switch has to recolour the existing lines in place rather than re-render them.
    /// </summary>
    private void ApplyLyricTheme()
    {
        if (_lyricLines.Count == 0)
        {
            return;
        }

        var foreground = CreateLyricForegroundBrush();
        foreach (var line in _lyricLines)
        {
            line.Block.Foreground = foreground;
        }
    }

    private void LyricLine_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetHoveredLyric(sender, hovered: true);

    private void LyricLine_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetHoveredLyric(sender, hovered: false);

    private void SetHoveredLyric(object sender, bool hovered)
    {
        if (sender is not Button { Tag: int index } ||
            index < 0 ||
            index >= _lyricLines.Count ||
            (hovered && _hoveredLyricIndex == index))
        {
            return;
        }

        _hoveredLyricIndex = hovered ? index : -1;
        ApplyLineState(_lyricLines[index], StateForLine(index), animate: true);
    }

    private LyricLineState StateForLine(int index) =>
        LyricPresentation.WithHover(
            LyricPresentation.StateForIndex(index, _currentLyricIndex, _lyricsAreTimeSynced),
            index == _hoveredLyricIndex);

    private async void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (!_lyricsAreTimeSynced ||
            sender is not Button { Tag: int index } ||
            index < 0 ||
            index >= _lyrics.Count)
        {
            return;
        }

        try
        {
            var startTime = _lyrics[index].StartTime;
            _optimisticSeekIndex = index;
            _optimisticSeekTarget = startTime;
            _optimisticSeekDeadline = DateTimeOffset.Now.AddSeconds(OptimisticSeekSeconds);
            SetCurrentLyricIndex(index, forceScroll: true);

            if (!await _ciderService.SeekAsync(startTime, _appToken, _lifetimeCancellation.Token))
            {
                _optimisticSeekIndex = -1;
                ReportCommandRejected("Cider 未接受跳转指令");
                return;
            }

            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private void LyricsScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_pendingAutoScrollOffset is { } pending)
        {
            var settled = !e.IsIntermediate &&
                Math.Abs(LyricsScrollViewer.VerticalOffset - pending) < 1;
            if (settled || DateTimeOffset.Now > _autoScrollDeadline)
            {
                _pendingAutoScrollOffset = null;
            }

            return;
        }

        if (e.IsIntermediate)
        {
            _lastManualLyricsScroll = DateTimeOffset.Now;
        }
    }

    private sealed class LyricLineVisual(
        FrameworkElement root,
        TextBlock block,
        ScaleTransform scale,
        LyricLineState state)
    {
        public FrameworkElement Root { get; } = root;

        public TextBlock Block { get; } = block;

        public ScaleTransform Scale { get; } = scale;

        public LyricLineState State { get; set; } = state;

        public Storyboard? Storyboard { get; set; }
    }

    private void QueueViewButton_Click(object sender, RoutedEventArgs e) => ShowPanel("Queue");

    private void LyricsViewButton_Click(object sender, RoutedEventArgs e) => ShowPanel("Lyrics");

    private async void MenuQueueItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowPlayerPanelAsync("Queue");
    }

    private async void MenuLyricsItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowPlayerPanelAsync("Lyrics");
    }

    private async void MenuSettingsItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsTransitioning || SettingsPageGrid.Visibility == Visibility.Visible)
        {
            return;
        }

        _isSettingsTransitioning = true;
        LoadSettingsControls();
        PlayerBackdropLayer.Visibility = Visibility.Collapsed;
        PlayerPageGrid.Visibility = Visibility.Collapsed;
        SettingsPageGrid.Visibility = Visibility.Visible;
        SettingsPageGrid.IsHitTestVisible = false;

        try
        {
            await AnimateSettingsPageAsync(0, 1, 16, 0, 220);
        }
        finally
        {
            SettingsPageGrid.IsHitTestVisible = true;
            _isSettingsTransitioning = false;
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExitRequested || !_settings.MinimizeToTrayOnClose)
        {
            return;
        }

        args.Cancel = true;
        _appWindow.Hide();
    }

    private void ShowFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            _appWindow.Show(true);
        });
    }

    private void ExitApplication()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isExitRequested = true;
            Close();
        });
    }

    private async Task ShowPlayerPanelAsync(string? panel)
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        if (SettingsPageGrid.Visibility == Visibility.Visible)
        {
            _isSettingsTransitioning = true;
            SettingsPageGrid.IsHitTestVisible = false;

            try
            {
                await AnimateSettingsPageAsync(1, 0, 0, 10, 160);
            }
            finally
            {
                SettingsPageGrid.Visibility = Visibility.Collapsed;
                SettingsPageGrid.Opacity = 1;
                SettingsPageTranslateTransform.Y = 0;
                SettingsPageGrid.IsHitTestVisible = true;
                _isSettingsTransitioning = false;
            }
        }

        PlayerBackdropLayer.Visibility = Visibility.Visible;
        PlayerPageGrid.Visibility = Visibility.Visible;
        if (panel is not null)
        {
            ShowPanel(panel);
        }
    }

    private async Task AnimateSettingsPageAsync(
        double fromOpacity,
        double toOpacity,
        double fromOffset,
        double toOffset,
        int durationMilliseconds)
    {
        SettingsPageGrid.Opacity = fromOpacity;
        SettingsPageTranslateTransform.Y = fromOffset;

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds));
        var opacityAnimation = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(opacityAnimation, SettingsPageGrid);
        Storyboard.SetTargetProperty(opacityAnimation, nameof(UIElement.Opacity));

        var offsetAnimation = new DoubleAnimation
        {
            From = fromOffset,
            To = toOffset,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(offsetAnimation, SettingsPageTranslateTransform);
        Storyboard.SetTargetProperty(offsetAnimation, nameof(TranslateTransform.Y));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnimation);
        storyboard.Children.Add(offsetAnimation);

        var completion = new TaskCompletionSource<bool>();
        storyboard.Completed += (_, _) => completion.TrySetResult(true);
        storyboard.Begin();
        await completion.Task;
        storyboard.Stop();

        SettingsPageGrid.Opacity = toOpacity;
        SettingsPageTranslateTransform.Y = toOffset;
    }

    private void ShowPanel(string panel)
    {
        var showLyrics = string.Equals(panel, "Lyrics", StringComparison.OrdinalIgnoreCase);
        QueuePanel.Visibility = showLyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsPanel.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
        QueueViewButton.Background = showLyrics
            ? new SolidColorBrush(Colors.Transparent)
            : CreateSelectedPanelBrush();
        LyricsViewButton.Background = showLyrics
            ? CreateSelectedPanelBrush()
            : new SolidColorBrush(Colors.Transparent);

        if (showLyrics)
        {
            // Visibility does not lay out synchronously, so wait until the panel has a
            // viewport before measuring the target line.
            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    UpdateLyricGutters();
                    ScrollToCurrentLyric(animate: false);
                });
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() => _ciderService.TogglePlayPauseAsync(
            _appToken,
            _lifetimeCancellation.Token));

    private async void PreviousButton_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() => _ciderService.PlayPreviousAsync(
            _appToken,
            _lifetimeCancellation.Token));

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() => _ciderService.PlayNextAsync(
            _appToken,
            _lifetimeCancellation.Token));

    private async void ShuffleButton_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() => _ciderService.ToggleShuffleAsync(
            _appToken,
            _lifetimeCancellation.Token));

    private async void RepeatButton_Click(object sender, RoutedEventArgs e) =>
        await RunPlaybackCommandAsync(() => _ciderService.ToggleRepeatAsync(
            _appToken,
            _lifetimeCancellation.Token));

    private async Task RunPlaybackCommandAsync(Func<Task<bool>> command)
    {
        if (!await command())
        {
            ReportCommandRejected();
            return;
        }

        await RefreshAsync(forceDetails: true);
    }

    private void ReportCommandRejected(string message = "Cider 未接受播放指令")
    {
        ConnectionStatusMenuItem.Text = message;
        ConnectionStatusIcon.Foreground = (Brush)RootGrid.Resources["DisconnectedBrush"];
    }

    private void TaskbarWidgetHost_CommandRequested(TaskbarPlaybackCommand command)
    {
        DispatcherQueue.TryEnqueue(async () => await RunTaskbarPlaybackCommandAsync(command));
    }

    private async Task RunTaskbarPlaybackCommandAsync(TaskbarPlaybackCommand command)
    {
        if (_isRunningTaskbarCommand)
        {
            return;
        }

        _isRunningTaskbarCommand = true;
        try
        {
            await RunPlaybackCommandAsync(command switch
            {
                TaskbarPlaybackCommand.Previous => () => _ciderService.PlayPreviousAsync(
                    _appToken,
                    _lifetimeCancellation.Token),
                TaskbarPlaybackCommand.TogglePlayPause => () => _ciderService.TogglePlayPauseAsync(
                    _appToken,
                    _lifetimeCancellation.Token),
                TaskbarPlaybackCommand.Next => () => _ciderService.PlayNextAsync(
                    _appToken,
                    _lifetimeCancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
            });
        }
        finally
        {
            _isRunningTaskbarCommand = false;
        }
    }

    private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _isUpdatingVolume)
        {
            return;
        }

        _volumeChangeCancellation?.Cancel();
        _volumeChangeCancellation?.Dispose();
        _volumeChangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var cancellationToken = _volumeChangeCancellation.Token;

        try
        {
            await Task.Delay(160, cancellationToken);
            await _ciderService.SetVolumeAsync(e.NewValue, _appToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer slider value superseded this request.
        }
    }

    private async void QueueListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QueueItemInfo item)
        {
            return;
        }

        await RunPlaybackCommandAsync(() => _ciderService.ChangeQueueIndexAsync(
            item.Index,
            _appToken,
            _lifetimeCancellation.Token));
    }

    private async void BackToPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowPlayerPanelAsync(null);
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        SetSettingsStatus("正在测试连接…", InfoBarSeverity.Informational);
        if (!Uri.TryCreate(ApiBaseUrlTextBox.Text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            SetSettingsStatus("API 地址必须是有效的 HTTP 或 HTTPS 地址", InfoBarSeverity.Error);
            return;
        }

        using var service = new CiderService(ApiBaseUrlTextBox.Text);
        var result = await service.GetNowPlayingAsync(
            NormalizeToken(TokenPasswordBox.Password),
            _lifetimeCancellation.Token);
        SetSettingsStatus(
            result.State == CiderConnectionState.Connected ? "连接成功" : result.Message,
            result.State == CiderConnectionState.Connected ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ciderService.TryUpdateBaseAddress(ApiBaseUrlTextBox.Text))
        {
            SetSettingsStatus("API 地址必须是有效的 HTTP 或 HTTPS 地址", InfoBarSeverity.Error);
            return;
        }

        var token = NormalizeToken(TokenPasswordBox.Password);
        if (!_tokenStore.TrySave(token))
        {
            SetSettingsStatus("无法保存 Token", InfoBarSeverity.Error);
            return;
        }

        _settings.ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim();
        _settings.Theme = SelectedTag(ThemeComboBox, "System");
        _settings.LyricFontSize = LyricFontSizeSlider.Value;
        _settings.AutoScrollLyrics = AutoScrollToggle.IsOn;
        _settings.AlwaysOnTop = AlwaysOnTopToggle.IsOn;
        _settings.TaskbarWidgetEnabled = TaskbarWidgetToggle.IsOn;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayToggle.IsOn;
        _settings.ShowVolume = ShowVolumeToggle.IsOn;
        _settings.DefaultPanel = SelectedTag(DefaultPanelComboBox, "Queue");
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value;

        if (!_settingsStore.TrySave(_settings))
        {
            SetSettingsStatus("无法保存应用设置", InfoBarSeverity.Error);
            return;
        }

        _appToken = token;
        ApplySettings();
        var taskbarWidgetUnsupported = _settings.TaskbarWidgetEnabled && !_taskbarWidgetHost.IsSupported;
        SetSettingsStatus(
            taskbarWidgetUnsupported ? "设置已保存；任务栏播放状态仅支持 Windows 11" : "设置已保存",
            taskbarWidgetUnsupported ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        await RefreshAsync(forceDetails: true);
    }

    private void LoadSavedToken()
    {
        if (_tokenStore.TryLoad(out var savedToken))
        {
            _appToken = savedToken;
            TokenPasswordBox.Password = savedToken ?? string.Empty;
            return;
        }

        ConnectionStatusMenuItem.Text = "无法读取已保存的 Token";
        ConnectionStatusIcon.Foreground = (Brush)RootGrid.Resources["DisconnectedBrush"];
    }

    private void LoadSettingsControls()
    {
        ApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
        TokenPasswordBox.Password = _appToken ?? string.Empty;
        SelectByTag(ThemeComboBox, _settings.Theme);
        SelectByTag(DefaultPanelComboBox, _settings.DefaultPanel);
        LyricFontSizeSlider.Value = _settings.LyricFontSize;
        AutoScrollToggle.IsOn = _settings.AutoScrollLyrics;
        AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;
        TaskbarWidgetToggle.IsOn = _settings.TaskbarWidgetEnabled;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTrayOnClose;
        ShowVolumeToggle.IsOn = _settings.ShowVolume;
        BackgroundOpacitySlider.Value = _settings.BackgroundOpacity;
        SetSettingsStatus(null);
    }

    private void SetSettingsStatus(string? message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        SettingsStatusInfoBar.Message = message ?? string.Empty;
        SettingsStatusInfoBar.Severity = severity;
        SettingsStatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private void ApplySettings()
    {
        RootGrid.RequestedTheme = _settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme();
        _trayIconHost.SetLightTheme(RootGrid.ActualTheme == ElementTheme.Light);
        BackgroundArtworkImage.Opacity = Math.Clamp(_settings.BackgroundOpacity, 0, 0.3);
        VolumePanel.Visibility = _settings.ShowVolume ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        }

        if (_settings.TaskbarWidgetEnabled && _taskbarWidgetHost.IsSupported)
        {
            _taskbarWidgetHost.Start();
        }
        else
        {
            _taskbarWidgetHost.Stop();
        }

        if (RenderLyrics())
        {
            UpdateCurrentLyric(_lastPlaybackTime, forceScroll: true);
        }
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private SolidColorBrush CreateSelectedPanelBrush() =>
        new(RootGrid.ActualTheme == ElementTheme.Light
            ? ColorHelper.FromArgb(0x19, 0, 0, 0)
            : ColorHelper.FromArgb(0x2A, 255, 255, 255));

    private static string? NormalizeToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    private static string GetTrackKey(NowPlayingInfo track) =>
        track.PlayParameters?.Id ?? $"{track.Name}\u001F{track.ArtistName}\u001F{track.AlbumName}";

    private static string? NormalizeArtworkUrl(string? url, int size = 800) =>
        url?
            .Replace("{w}", size.ToString(), StringComparison.Ordinal)
            .Replace("{h}", size.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static string ValueOrFallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= AppWindow_Closing;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        RootGrid.XamlRoot.Changed -= XamlRoot_Changed;
        _refreshTimer.Stop();
        _volumeChangeCancellation?.Cancel();
        _volumeChangeCancellation?.Dispose();
        _lifetimeCancellation.Cancel();
        _taskbarWidgetHost.CommandRequested -= TaskbarWidgetHost_CommandRequested;
        _taskbarWidgetHost.Dispose();
        _trayIconHost.Dispose();
        _lifetimeCancellation.Dispose();
        _lyricsService.Dispose();
        _ciderService.Dispose();
    }
}
