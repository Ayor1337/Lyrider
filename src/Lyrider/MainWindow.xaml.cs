using System.Collections.ObjectModel;
using System.Diagnostics;
using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace Lyrider;

public sealed partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly TokenStore _tokenStore = new();
    private readonly MusixmatchKeyStore _musixmatchKeyStore = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly DispatcherTimer _lyricTimer = new();
    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
    private readonly PlaybackTimeline _playbackTimeline = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ObservableCollection<QueueItemInfo> _queueItems = [];
    private readonly List<LyricLineVisual> _lyricLines = [];
    private readonly SolidColorBrush _transparentLyricBackground = new(Colors.Transparent);
    private readonly TaskbarWidgetHost _taskbarWidgetHost = new();
    private readonly LyricsService _lyricsService = new();
    private readonly CiderService _ciderService;
    private readonly NextTrackLyricsPreloader _nextTrackLyricsPreloader;
    private readonly ArtworkPresenter _artworkPresenter;
    private readonly AppWindow _appWindow;
    private readonly TrayIconHost _trayIconHost;

    private AppSettings _settings;
    private IReadOnlyList<LyricLineInfo> _lyrics = [];
    private CancellationTokenSource? _playbackSeekDebounceCancellation;
    private CancellationTokenSource? _lyricsRefreshCancellation;
    private string? _appToken;
    private string? _musixmatchApiKey;
    private string? _validatedOnboardingApiBaseUrl;
    private string? _validatedOnboardingToken;
    private string? _currentTrackKey;
    private QueueItemInfo? _nextQueueItem;
    private NowPlayingInfo? _latestTrack;
    private PlaybackStatus? _latestPlaybackStatus;
    private bool _isRefreshing;
    private bool _isUpdatingPlaybackProgress;
    private bool _isDraggingPlaybackProgress;
    private bool _lyricsAreTimeSynced;
    private bool _isInitialized;
    private bool _isRunningTaskbarCommand;
    private bool _isExitRequested;
    private bool _isSettingsTransitioning;
    private bool _isRunningSettingsAction;
    private bool _isRunningOnboardingAction;
    private DateTimeOffset? _startupLoadingStartedAt;
    private int _currentLyricIndex = -1;
    private int _refreshCount;
    private int _renderedLyricsSignature;
    private double _lastPlaybackTime;
    private int _optimisticSeekIndex = -1;
    private int _hoveredLyricIndex = -1;
    private double _optimisticSeekTarget;
    private double? _optimisticPlaybackPosition;
    private double? _pendingAutoScrollOffset;
    private DateTimeOffset _lastManualLyricsScroll = DateTimeOffset.MinValue;
    private DateTimeOffset _autoScrollDeadline;
    private DateTimeOffset _optimisticSeekDeadline;
    private DateTimeOffset _optimisticPlaybackDeadline;

    private const int LyricAnimationMilliseconds = 180;
    private const int AutoScrollSettleMilliseconds = 700;
    private const int StartupLoadingMinimumMilliseconds = 450;
    private const int StartupLoadingExitMilliseconds = 220;
    private const double OptimisticSeekSeconds = 2.5;
    private const double OptimisticSeekToleranceSeconds = 1.5;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _ciderService = new CiderService(_settings.ApiBaseUrl);
        _nextTrackLyricsPreloader = new NextTrackLyricsPreloader(_ciderService, _lyricsService);

        InitializeComponent();
        PlaybackProgressSlider.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(PlaybackProgressSlider_PointerPressed),
            true);
        PlaybackProgressSlider.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(PlaybackProgressSlider_PointerReleased),
            true);
        PlaybackProgressSlider.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(PlaybackProgressSlider_PointerCaptureLost),
            true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _artworkPresenter = new ArtworkPresenter(
            BackgroundArtworkHost,
            ArtworkImage,
            CurrentQueueArtworkImage);

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
        LoadSavedSecrets();
        LoadSettingsControls();
        InitializeOnboarding();
        ApplySettings();
        ShowPanel(_settings.DefaultPanel);

        _refreshTimer.Tick += RefreshTimer_Tick;
        _lyricTimer.Tick += LyricTimer_Tick;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
        _isInitialized = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _startupLoadingStartedAt ??= DateTimeOffset.UtcNow;
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
        var artworkSize = Math.Max(0, Math.Min(420, Math.Min(playerWidth, playerHeight - 170)));
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

        foreach (var row in new[] { ThemeSettingsRow, LanguageSettingsRow, BackgroundSettingsRow, BackgroundBlurSettingsRow,
            LyricFontSettingsRow, LyricsSourceSettingsRow, LyricsTranslationSettingsRow, MusixmatchSettingsRow,
            ChineseLyricsSettingsRow, AutoScrollSettingsRow, DefaultPanelSettingsRow,
            AlwaysOnTopSettingsRow, TaskbarWidgetSettingsRow, TaskbarLyricsSettingsRow,
            MinimizeToTraySettingsRow })
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
        try
        {
            if (OnboardingPageGrid.Visibility != Visibility.Visible)
            {
                await RefreshAsync(forceDetails: true);
            }
        }
        finally
        {
            await HideStartupLoadingAsync();
            if (OnboardingPageGrid.Visibility != Visibility.Visible)
            {
                _refreshTimer.Start();
            }
        }
    }

    private void InitializeOnboarding()
    {
        OnboardingApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
        OnboardingTokenPasswordBox.Password = _appToken ?? string.Empty;

        // Users upgrading from an earlier release already completed the equivalent setup
        // when they saved a token in Settings, so do not interrupt them with the new guide.
        if (!_settings.HasCompletedOnboarding && !string.IsNullOrWhiteSpace(_appToken))
        {
            _settings.HasCompletedOnboarding = true;
            _settingsStore.TrySave(_settings);
        }

        OnboardingPageGrid.Visibility = _settings.HasCompletedOnboarding
            ? Visibility.Collapsed
            : Visibility.Visible;
        ValidateOnboardingButton.IsEnabled = !string.IsNullOrWhiteSpace(OnboardingTokenPasswordBox.Password);
    }

    private async Task HideStartupLoadingAsync()
    {
        if (StartupLoadingOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var startedAt = _startupLoadingStartedAt ?? DateTimeOffset.UtcNow;
        var minimumDuration = TimeSpan.FromMilliseconds(StartupLoadingMinimumMilliseconds);
        var remaining = minimumDuration - (DateTimeOffset.UtcNow - startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(StartupLoadingExitMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnimation = new DoubleAnimation
        {
            From = StartupLoadingOverlay.Opacity,
            To = 0,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(opacityAnimation, StartupLoadingOverlay);
        Storyboard.SetTargetProperty(opacityAnimation, nameof(UIElement.Opacity));

        var offsetAnimation = new DoubleAnimation
        {
            From = StartupLoadingTranslateTransform.Y,
            To = -8,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(offsetAnimation, StartupLoadingTranslateTransform);
        Storyboard.SetTargetProperty(offsetAnimation, nameof(TranslateTransform.Y));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnimation);
        storyboard.Children.Add(offsetAnimation);
        var completion = new TaskCompletionSource<bool>();
        storyboard.Completed += (_, _) => completion.TrySetResult(true);
        storyboard.Begin();
        await completion.Task;
        storyboard.Stop();

        StartupProgressRing.IsActive = false;
        StartupLoadingOverlay.IsHitTestVisible = false;
        StartupLoadingOverlay.Visibility = Visibility.Collapsed;
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
            var trackKey = TrackIdentity.For(track);
            var trackChanged = !string.Equals(trackKey, _currentTrackKey, StringComparison.Ordinal);
            SynchronizePlaybackTimeline(track);
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
                RefreshLyricPlayback();
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
        _lyricsRefreshCancellation?.Cancel();
        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _lyricsRefreshCancellation = refreshCancellation;
        var expectedTrackKey = TrackIdentity.For(track);
        var options = CurrentLyricsOptions();

        try
        {
            var trackId = track.PlayParameters?.Id;
            var queueTask = _ciderService.GetQueueAsync(
                _appToken,
                trackId,
                refreshCancellation.Token);
            var lyricsTask = ResolveTrackLyricsAsync(
                track,
                options,
                refreshCancellation.Token);

            var queue = await queueTask;
            if (!string.Equals(expectedTrackKey, _currentTrackKey, StringComparison.Ordinal))
            {
                return;
            }

            ApplyQueue(queue, trackId);
            var lyrics = await lyricsTask;
            if (!string.Equals(expectedTrackKey, _currentTrackKey, StringComparison.Ordinal))
            {
                return;
            }

            _lyrics = lyrics.Lines;
            _lyricsAreTimeSynced = lyrics.IsTimeSynced;
            var rebuilt = RenderLyrics();
            RefreshLyricPlayback(forceScroll: rebuilt);
            PrepareNextLyrics();
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
            // A newer track/settings refresh superseded this request, or the window is closing.
        }
        finally
        {
            if (ReferenceEquals(_lyricsRefreshCancellation, refreshCancellation))
            {
                _lyricsRefreshCancellation = null;
            }

            refreshCancellation.Dispose();
        }
    }

    private async Task RefreshQueueAsync(NowPlayingInfo track)
    {
        var queue = await _ciderService.GetQueueAsync(
            _appToken,
            track.PlayParameters?.Id,
            _lifetimeCancellation.Token);
        ApplyQueue(queue, track.PlayParameters?.Id);
        PrepareNextLyrics();
    }

    private async Task<LyricsSnapshot> ResolveTrackLyricsAsync(
        NowPlayingInfo track,
        LyricsResolveOptions options,
        CancellationToken cancellationToken)
    {
        var prefetched = await _nextTrackLyricsPreloader.TakeAsync(
            track,
            options,
            _appToken,
            cancellationToken);
        if (prefetched is not null)
        {
            return prefetched;
        }

        var ciderLyrics = await _ciderService.GetLyricsAsync(
            track.PlayParameters?.Id,
            _appToken,
            cancellationToken);
        return await _lyricsService.ResolveAsync(track, ciderLyrics, options, cancellationToken);
    }

    private LyricsResolveOptions CurrentLyricsOptions() => new(
        LyricsService.ParseSource(_settings.LyricsSource),
        _settings.ShowLyricsTranslation,
        _musixmatchApiKey);

    private void PrepareNextLyrics() =>
        _nextTrackLyricsPreloader.Prepare(_nextQueueItem, CurrentLyricsOptions(), _appToken);

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

        _latestPlaybackStatus = status;
        PlayPauseIcon.Glyph = status.IsPlaying ? "\uE769" : "\uE768";
    }

    private void SynchronizePlaybackTimeline(NowPlayingInfo track)
    {
        _latestTrack = track;
        _playbackTimeline.Synchronize(
            track.CurrentPlaybackTime,
            track.DurationInMillis / 1000,
            _latestPlaybackStatus?.IsPlaying ?? false,
            _playbackClock.Elapsed);
    }

    private void UpdateNowPlaying(NowPlayingInfo? track)
    {
        if (track is null)
        {
            _lyricTimer.Stop();
            _playbackTimeline.Reset();
            _latestTrack = null;
            _currentTrackKey = null;
            _nextQueueItem = null;
            _nextTrackLyricsPreloader.Clear();
            SongNameText.Text = AppText.Get("未在播放", "Not Playing");
            ArtistAlbumText.Text = "—";
            CurrentQueueSongText.Text = AppText.Get("未在播放", "Not Playing");
            CurrentQueueArtistText.Text = "—";
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = "−0:00";
            SetPlaybackProgress(0, 0);
            ClearOptimisticPlaybackPosition();
            _artworkPresenter.PrepareNext(null);
            _artworkPresenter.Show(null);
            ApplyQueue(new QueueSnapshot([], -1));
            _lyrics = [];
            _lastPlaybackTime = 0;
            RenderLyrics();
            return;
        }

        var durationSeconds = Math.Max(0, track.DurationInMillis / 1000);
        var currentSeconds = Math.Clamp(track.CurrentPlaybackTime, 0, durationSeconds);
        if (!string.Equals(TrackIdentity.For(track), _currentTrackKey, StringComparison.Ordinal))
        {
            ClearOptimisticPlaybackPosition();
        }

        _lastPlaybackTime = currentSeconds;
        var songName = ValueOrFallback(track.Name);
        var artistName = ValueOrFallback(track.ArtistName);
        var albumName = ValueOrFallback(track.AlbumName);

        SongNameText.Text = songName;
        ArtistAlbumText.Text = $"{albumName} — {artistName}";
        CurrentQueueSongText.Text = songName;
        CurrentQueueArtistText.Text = $"{artistName} — {albumName}";
        var displayedSeconds = ResolveDisplayedPlaybackPosition(currentSeconds, durationSeconds);
        SetPlaybackProgress(displayedSeconds, durationSeconds);
        ShuffleButton.Opacity = track.ShuffleMode > 0 ? 1 : 0.55;
        UpdateRepeatButton(track.RepeatMode);
        _artworkPresenter.Show(NormalizeArtworkUrl(track.Artwork?.Url));
    }

    private void UpdateRepeatButton(int repeatMode)
    {
        var state = RepeatPresentation.ForMode(repeatMode);
        RepeatIcon.Opacity = state.IconOpacity;
        RepeatOneBadge.Visibility = state.ShowOneBadge ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(RepeatButton, state.Label);
        AutomationProperties.SetName(RepeatButton, state.Label);
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

        var hasCurrentLyric =
            _settings.ShowLyricsInTaskbar &&
            _lyricsAreTimeSynced &&
            _currentLyricIndex >= 0;
        var translation = hasCurrentLyric &&
            !string.IsNullOrWhiteSpace(_lyrics[_currentLyricIndex].Translation)
                ? DisplayLyricText(DisplayTranslationText(_lyrics[_currentLyricIndex].Translation!))
                : null;
        var nextLyric = hasCurrentLyric && _currentLyricIndex + 1 < _lyrics.Count
            ? DisplayLyricText(_lyrics[_currentLyricIndex + 1].Text)
            : null;

        _taskbarWidgetHost.Update(new TaskbarPlaybackState(
            ValueOrFallback(track.Name),
            ValueOrFallback(track.ArtistName),
            NormalizeArtworkUrl(track.Artwork?.Url, 160),
            status?.IsPlaying ?? false,
            true,
            hasCurrentLyric
                ? DisplayLyricText(_lyrics[_currentLyricIndex].Text)
                : null,
            TaskbarPresentation.SelectSecondaryLyric(
                _settings.ShowLyricsTranslation,
                translation,
                nextLyric),
            hasCurrentLyric
                ? _currentLyricIndex
                : null));
    }

    private void ApplyQueue(QueueSnapshot snapshot, string? currentTrackId = null)
    {
        _nextQueueItem = NextTrackLyricsPreloader.SelectNext(snapshot, currentTrackId);
        _artworkPresenter.PrepareNext(NormalizeArtworkUrl(_nextQueueItem?.ArtworkUrl));

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
                NormalizeArtworkUrl(item.ArtworkUrl, 160),
                item.HasLyrics,
                item.HasTimeSyncedLyrics))
            .ToArray();
        QueueCollectionSynchronizer.Synchronize(_queueItems, desiredItems);

        QueueCountText.Text = AppText.IsChinese
            ? $"{_queueItems.Count} 首"
            : $"{_queueItems.Count} {(_queueItems.Count == 1 ? "song" : "songs")}";
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
            _lyricsAreTimeSynced,
            _settings.ConvertTraditionalLyricsToSimplified,
            _settings.ShowLyricsTranslation);
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
            var displayText = DisplayLyricText(line.Text);
            var block = new TextBlock
            {
                Text = displayText,
                FontSize = _settings.LyricFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var textPanel = new StackPanel
            {
                Spacing = 4,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            textPanel.Children.Add(block);
            TextBlock? translationBlock = null;
            if (_settings.ShowLyricsTranslation && !string.IsNullOrWhiteSpace(line.Translation))
            {
                translationBlock = new TextBlock
                {
                    Text = DisplayTranslationText(line.Translation),
                    FontSize = Math.Max(14, _settings.LyricFontSize * 0.6),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = foreground,
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 820,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                textPanel.Children.Add(translationBlock);
            }

            var state = StateForLine(index);
            var scale = new ScaleTransform { ScaleX = state.Scale, ScaleY = state.Scale };
            FrameworkElement root = textPanel;
            if (seekable)
            {
                var button = new Button
                {
                    Style = (Style)RootGrid.Resources["LyricLineButtonStyle"],
                    Content = textPanel,
                    Tag = index
                };

                // The template's hover/press states paint a pill behind the text; pointer
                // feedback is a brighter line instead, so keep those fills transparent.
                button.Resources["ButtonBackgroundPointerOver"] = _transparentLyricBackground;
                button.Resources["ButtonBackgroundPressed"] = _transparentLyricBackground;
                button.PointerEntered += LyricLine_PointerEntered;
                button.PointerExited += LyricLine_PointerExited;
                button.Click += LyricLine_Click;
                var seekLabel = AppText.Format("跳转到 {0}", "Seek to {0}", FormatTime(line.StartTime));
                ToolTipService.SetToolTip(button, seekLabel);
                AutomationProperties.SetName(
                    button,
                    translationBlock is null ? displayText : $"{displayText}\n{translationBlock.Text}");
                AutomationProperties.SetHelpText(button, seekLabel);
                root = button;
            }

            root.Opacity = state.Opacity;
            root.RenderTransform = scale;
            root.RenderTransformOrigin = new Point(0, 0.5);
            _lyricLines.Add(new LyricLineVisual(root, block, translationBlock, scale, state));
            LyricsStackPanel.Children.Add(root);
        }

        LyricsStackPanel.Children.Add(LyricsBottomSpacer);
        UpdateLyricGutters();
        return true;
    }

    private bool UpdateCurrentLyric(double playbackTime, bool forceScroll = false)
    {
        if (!_lyricsAreTimeSynced || _lyrics.Count == 0 || _lyricLines.Count == 0)
        {
            return false;
        }

        if (!ShouldApplyServerPosition(playbackTime))
        {
            return false;
        }

        var nextIndex = LyricPresentation.FindActiveLineIndex(_lyrics, playbackTime);
        if (nextIndex < 0 || (nextIndex == _currentLyricIndex && !forceScroll))
        {
            return false;
        }

        SetCurrentLyricIndex(nextIndex, forceScroll);
        return true;
    }

    private void LyricTimer_Tick(object? sender, object e)
    {
        _lyricTimer.Stop();
        RefreshLyricPlayback();
    }

    private void RefreshLyricPlayback(bool forceScroll = false)
    {
        if (_latestTrack is null)
        {
            _lyricTimer.Stop();
            return;
        }

        var playbackTime = _playbackTimeline.PositionAt(_playbackClock.Elapsed);
        _lastPlaybackTime = playbackTime;
        if (UpdateCurrentLyric(playbackTime, forceScroll))
        {
            UpdateTaskbarWidget(_latestTrack, _latestPlaybackStatus);
        }

        ScheduleNextLyricUpdate(playbackTime);
    }

    private void ScheduleNextLyricUpdate(double playbackTime)
    {
        _lyricTimer.Stop();
        if (_latestPlaybackStatus?.IsPlaying != true || !_lyricsAreTimeSynced)
        {
            return;
        }

        var delay = LyricPresentation.DelayUntilNextLine(_lyrics, playbackTime);
        if (delay is null)
        {
            return;
        }

        _lyricTimer.Interval = delay.Value < TimeSpan.FromMilliseconds(1)
            ? TimeSpan.FromMilliseconds(1)
            : delay.Value;
        _lyricTimer.Start();
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
            if (line.TranslationBlock is not null)
            {
                line.TranslationBlock.Foreground = foreground;
            }
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
                ReportCommandRejected(AppText.Get("Cider 未接受跳转指令", "Cider did not accept the seek command"));
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
        TextBlock? translationBlock,
        ScaleTransform scale,
        LyricLineState state)
    {
        public FrameworkElement Root { get; } = root;

        public TextBlock Block { get; } = block;

        public TextBlock? TranslationBlock { get; } = translationBlock;

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

    private void ReportCommandRejected(string? message = null)
    {
        ConnectionStatusMenuItem.Text = message ?? AppText.Get(
            "Cider 未接受播放指令",
            "Cider did not accept the playback command");
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
        if (!BeginSettingsAction())
        {
            return;
        }

        try
        {
            await TestConnectionAsync();
        }
        finally
        {
            EndSettingsAction();
        }
    }

    private void OnboardingTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized && !_isRunningOnboardingAction)
        {
            InvalidateOnboardingValidation();
        }
    }

    private void OnboardingApiBaseUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized && !_isRunningOnboardingAction)
        {
            InvalidateOnboardingValidation();
        }
    }

    private async void ValidateOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOnboardingAction())
        {
            return;
        }

        try
        {
            var apiBaseUrl = OnboardingApiBaseUrlTextBox.Text.Trim();
            if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                ShowOnboardingStatus(
                    InfoBarSeverity.Error,
                    AppText.Get("API 地址无效", "Invalid API address"),
                    AppText.Get("请输入有效的 HTTP 或 HTTPS 地址。", "Enter a valid HTTP or HTTPS address."));
                return;
            }

            var token = NormalizeToken(OnboardingTokenPasswordBox.Password);
            if (token is null)
            {
                ShowOnboardingStatus(
                    InfoBarSeverity.Warning,
                    AppText.Get("需要 App Token", "App Token required"),
                    AppText.Get("请先粘贴从 Cider 复制的 Token。", "Paste the token copied from Cider first."));
                return;
            }

            ShowOnboardingStatus(
                InfoBarSeverity.Informational,
                AppText.Get("正在验证连接", "Verifying connection"),
                AppText.Get("请保持 Cider 运行。", "Keep Cider running."));

            using var service = new CiderService(apiBaseUrl);
            var result = await service.GetNowPlayingAsync(token, _lifetimeCancellation.Token);
            if (result.State != CiderConnectionState.Connected)
            {
                ShowOnboardingStatus(
                    InfoBarSeverity.Error,
                    AppText.Get("无法连接 Cider", "Could not connect to Cider"),
                    result.Message);
                return;
            }

            _validatedOnboardingApiBaseUrl = apiBaseUrl;
            _validatedOnboardingToken = token;
            ValidateOnboardingButton.Visibility = Visibility.Collapsed;
            ConfirmOnboardingButton.Visibility = Visibility.Visible;
            ShowOnboardingStatus(
                InfoBarSeverity.Success,
                AppText.Get("连接成功", "Connection successful"),
                AppText.Get(
                    "已成功连接到 Cider。确认后将保存 Token 并进入 Lyrider。",
                    "Connected to Cider. Confirm to save the token and continue to Lyrider."));
        }
        finally
        {
            EndOnboardingAction();
        }
    }

    private async void ConfirmOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOnboardingAction())
        {
            return;
        }

        try
        {
            var apiBaseUrl = _validatedOnboardingApiBaseUrl;
            var token = _validatedOnboardingToken;
            if (apiBaseUrl is null || token is null)
            {
                InvalidateOnboardingValidation();
                return;
            }

            if (!_tokenStore.TrySave(token))
            {
                ShowOnboardingStatus(
                    InfoBarSeverity.Error,
                    AppText.Get("无法保存 Token", "Could not save the token"),
                    AppText.Get(
                        "Windows 无法加密并保存这个 Token，请重试。",
                        "Windows could not encrypt and save this token. Try again."));
                return;
            }

            var previousApiBaseUrl = _settings.ApiBaseUrl;
            _settings.ApiBaseUrl = apiBaseUrl;
            _settings.HasCompletedOnboarding = true;
            if (!_settingsStore.TrySave(_settings))
            {
                _tokenStore.TrySave(_appToken);
                _settings.ApiBaseUrl = previousApiBaseUrl;
                _settings.HasCompletedOnboarding = false;
                ShowOnboardingStatus(
                    InfoBarSeverity.Error,
                    AppText.Get("无法保存设置", "Could not save settings"),
                    AppText.Get("应用设置未能写入本机，请重试。", "The app settings could not be saved. Try again."));
                return;
            }

            _ciderService.TryUpdateBaseAddress(apiBaseUrl);
            _appToken = token;
            LoadSettingsControls();
            await FinishOnboardingAsync();
        }
        finally
        {
            EndOnboardingAction();
        }
    }

    private async void SkipOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOnboardingAction())
        {
            return;
        }

        try
        {
            _settings.HasCompletedOnboarding = true;
            if (!_settingsStore.TrySave(_settings))
            {
                _settings.HasCompletedOnboarding = false;
                ShowOnboardingStatus(
                    InfoBarSeverity.Error,
                    AppText.Get("无法保存设置", "Could not save settings"),
                    AppText.Get("无法记录引导状态，请重试。", "The setup status could not be saved. Try again."));
                return;
            }

            await FinishOnboardingAsync();
        }
        finally
        {
            EndOnboardingAction();
        }
    }

    private bool BeginOnboardingAction()
    {
        if (_isRunningOnboardingAction)
        {
            return false;
        }

        _isRunningOnboardingAction = true;
        ValidateOnboardingButton.IsEnabled = false;
        ConfirmOnboardingButton.IsEnabled = false;
        SkipOnboardingButton.IsEnabled = false;
        return true;
    }

    private void EndOnboardingAction()
    {
        _isRunningOnboardingAction = false;
        ValidateOnboardingButton.IsEnabled =
            !string.IsNullOrWhiteSpace(OnboardingTokenPasswordBox.Password);
        ConfirmOnboardingButton.IsEnabled =
            _validatedOnboardingApiBaseUrl is not null && _validatedOnboardingToken is not null;
        SkipOnboardingButton.IsEnabled = true;
    }

    private void InvalidateOnboardingValidation()
    {
        _validatedOnboardingApiBaseUrl = null;
        _validatedOnboardingToken = null;
        ConfirmOnboardingButton.Visibility = Visibility.Collapsed;
        ValidateOnboardingButton.Visibility = Visibility.Visible;
        ValidateOnboardingButton.IsEnabled =
            !string.IsNullOrWhiteSpace(OnboardingTokenPasswordBox.Password);
        OnboardingStatusInfoBar.IsOpen = false;
    }

    private void ShowOnboardingStatus(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        OnboardingStatusInfoBar.Severity = severity;
        OnboardingStatusInfoBar.Title = title;
        OnboardingStatusInfoBar.Message = message;
        OnboardingStatusInfoBar.IsOpen = true;
    }

    private async Task FinishOnboardingAsync()
    {
        OnboardingStatusInfoBar.IsOpen = false;
        OnboardingPageGrid.Visibility = Visibility.Collapsed;
        await RefreshAsync(forceDetails: true);
        _refreshTimer.Start();
    }

    private void PlaybackProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PlaybackProgressSlider.Maximum <= 1)
        {
            return;
        }

        _isDraggingPlaybackProgress = true;
        CancelPlaybackSeekDebounce();
    }

    private async void PlaybackProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        await CompletePlaybackProgressDragAsync();

    private async void PlaybackProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        await CompletePlaybackProgressDragAsync();

    private async Task CompletePlaybackProgressDragAsync()
    {
        if (!_isDraggingPlaybackProgress)
        {
            return;
        }

        _isDraggingPlaybackProgress = false;
        await SeekPlaybackAsync(PlaybackProgressSlider.Value);
    }

    private async void PlaybackProgressSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingPlaybackProgress)
        {
            return;
        }

        UpdatePlaybackTimeText(e.NewValue, PlaybackProgressSlider.Maximum);
        if (!_isInitialized || _isDraggingPlaybackProgress)
        {
            return;
        }

        CancelPlaybackSeekDebounce();
        _playbackSeekDebounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var cancellationToken = _playbackSeekDebounceCancellation.Token;
        try
        {
            await Task.Delay(250, cancellationToken);
            await SeekPlaybackAsync(e.NewValue, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer keyboard adjustment or pointer drag superseded this request.
        }
    }

    private async Task SeekPlaybackAsync(
        double position,
        CancellationToken cancellationToken = default)
    {
        var target = Math.Clamp(position, 0, PlaybackProgressSlider.Maximum);
        _optimisticPlaybackPosition = target;
        _optimisticPlaybackDeadline = DateTimeOffset.Now.AddSeconds(OptimisticSeekSeconds);
        UpdatePlaybackTimeText(target, PlaybackProgressSlider.Maximum);

        var token = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _lifetimeCancellation.Token;
        try
        {
            if (!await _ciderService.SeekAsync(target, _appToken, token))
            {
                if (_optimisticPlaybackPosition == target)
                {
                    _optimisticPlaybackPosition = null;
                }

                ReportCommandRejected(AppText.Get("Cider 未接受跳转指令", "Cider did not accept the seek command"));
            }

            await RefreshAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_optimisticPlaybackPosition == target)
            {
                _optimisticPlaybackPosition = null;
            }
        }
    }

    private double ResolveDisplayedPlaybackPosition(double serverPosition, double duration)
    {
        if (_isDraggingPlaybackProgress)
        {
            return Math.Clamp(PlaybackProgressSlider.Value, 0, duration);
        }

        if (_optimisticPlaybackPosition is not double target)
        {
            return serverPosition;
        }

        if (Math.Abs(serverPosition - target) <= OptimisticSeekToleranceSeconds ||
            DateTimeOffset.Now > _optimisticPlaybackDeadline)
        {
            _optimisticPlaybackPosition = null;
            return serverPosition;
        }

        return Math.Clamp(target, 0, duration);
    }

    private void SetPlaybackProgress(double position, double duration)
    {
        _isUpdatingPlaybackProgress = true;
        PlaybackProgressSlider.Maximum = Math.Max(1, duration);
        PlaybackProgressSlider.Value = Math.Clamp(position, 0, PlaybackProgressSlider.Maximum);
        _isUpdatingPlaybackProgress = false;
        UpdatePlaybackTimeText(position, duration);
    }

    private void UpdatePlaybackTimeText(double position, double duration)
    {
        var current = Math.Clamp(position, 0, Math.Max(0, duration));
        CurrentTimeText.Text = FormatTime(current);
        RemainingTimeText.Text = $"−{FormatTime(Math.Max(0, duration - current))}";
    }

    private void CancelPlaybackSeekDebounce()
    {
        _playbackSeekDebounceCancellation?.Cancel();
        _playbackSeekDebounceCancellation?.Dispose();
        _playbackSeekDebounceCancellation = null;
    }

    private void ClearOptimisticPlaybackPosition()
    {
        _isDraggingPlaybackProgress = false;
        _optimisticPlaybackPosition = null;
        CancelPlaybackSeekDebounce();
    }

    private async Task TestConnectionAsync()
    {
        if (!Uri.TryCreate(ApiBaseUrlTextBox.Text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            await ShowSettingsDialogAsync(
                AppText.Get("连接测试", "Connection test"),
                AppText.Get("API 地址必须是有效的 HTTP 或 HTTPS 地址", "The API address must be a valid HTTP or HTTPS address"));
            return;
        }

        TestConnectionButton.Content = AppText.Get("正在测试…", "Testing…");
        try
        {
            using var service = new CiderService(ApiBaseUrlTextBox.Text);
            var result = await service.GetNowPlayingAsync(
                NormalizeToken(TokenPasswordBox.Password),
                _lifetimeCancellation.Token);
            await ShowSettingsDialogAsync(
                result.State == CiderConnectionState.Connected
                    ? AppText.Get("连接成功", "Connection successful")
                    : AppText.Get("连接失败", "Connection failed"),
                result.State == CiderConnectionState.Connected
                    ? AppText.Get("已成功连接到 Cider 本地 API。", "Connected to the local Cider API.")
                    : result.Message);
        }
        finally
        {
            TestConnectionButton.Content = AppText.Get("测试连接", "Test Connection");
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginSettingsAction())
        {
            return;
        }

        try
        {
            await SaveSettingsAsync();
        }
        finally
        {
            EndSettingsAction();
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!_ciderService.TryUpdateBaseAddress(ApiBaseUrlTextBox.Text))
        {
            await ShowSettingsDialogAsync(
                AppText.Get("无法保存设置", "Could not save settings"),
                AppText.Get("API 地址必须是有效的 HTTP 或 HTTPS 地址", "The API address must be a valid HTTP or HTTPS address"));
            return;
        }

        _nextTrackLyricsPreloader.Clear();
        _nextQueueItem = null;
        _artworkPresenter.PrepareNext(null);

        var lyricsSource = LyricsService.ParseSource(SelectedTag(LyricsSourceComboBox, "Auto"));
        var musixmatchApiKey = NormalizeToken(MusixmatchApiKeyPasswordBox.Password);
        if (lyricsSource == LyricsSource.Musixmatch && musixmatchApiKey is null)
        {
            await ShowSettingsDialogAsync(
                AppText.Get("无法保存设置", "Could not save settings"),
                AppText.Get(
                    "选择 Musixmatch 歌词源时必须填写 API Key",
                    "An API Key is required when Musixmatch is selected as the lyrics source"));
            return;
        }

        var token = NormalizeToken(TokenPasswordBox.Password);
        if (!_tokenStore.TrySave(token))
        {
            await ShowSettingsDialogAsync(
                AppText.Get("无法保存设置", "Could not save settings"),
                AppText.Get("无法保存 Token", "Could not save the token"));
            return;
        }

        if (!_musixmatchKeyStore.TrySave(musixmatchApiKey))
        {
            await ShowSettingsDialogAsync(
                AppText.Get("无法保存设置", "Could not save settings"),
                AppText.Get(
                    "无法保存 Musixmatch API Key",
                    "Could not save the Musixmatch API Key"));
            return;
        }

        _settings.ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim();
        _settings.Theme = SelectedTag(ThemeComboBox, "System");
        var previousLanguage = _settings.Language;
        _settings.Language = SelectedTag(LanguageComboBox, "System");
        _settings.LyricFontSize = LyricFontSizeSlider.Value;
        _settings.AutoScrollLyrics = AutoScrollToggle.IsOn;
        _settings.ConvertTraditionalLyricsToSimplified = ChineseLyricsToggle.IsOn;
        _settings.LyricsSource = lyricsSource.ToString();
        _settings.ShowLyricsTranslation =
            LyricsService.SupportsTranslation(lyricsSource) && LyricsTranslationToggle.IsOn;
        _settings.AlwaysOnTop = AlwaysOnTopToggle.IsOn;
        _settings.TaskbarWidgetEnabled = TaskbarWidgetToggle.IsOn;
        _settings.ShowLyricsInTaskbar = TaskbarLyricsToggle.IsOn;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayToggle.IsOn;
        _settings.DefaultPanel = SelectedTag(DefaultPanelComboBox, "Queue");
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value / 100;
        _settings.BackgroundBlur = BackgroundBlurSlider.Value;

        if (!_settingsStore.TrySave(_settings))
        {
            await ShowSettingsDialogAsync(
                AppText.Get("无法保存设置", "Could not save settings"),
                AppText.Get("无法保存应用设置", "Could not save the app settings"));
            return;
        }

        _appToken = token;
        _musixmatchApiKey = musixmatchApiKey;
        ApplySettings();
        var taskbarWidgetUnsupported = _settings.TaskbarWidgetEnabled && !_taskbarWidgetHost.IsSupported;
        await RefreshAsync(forceDetails: true);
        await ShowSettingsDialogAsync(
            AppText.Get("设置已保存", "Settings saved"),
            !string.Equals(previousLanguage, _settings.Language, StringComparison.Ordinal)
                ? AppText.Get(
                    "应用设置已更新；界面语言将在重启 Lyrider 后生效。",
                    "App settings updated. Restart Lyrider to apply the display language.")
                : taskbarWidgetUnsupported
                ? AppText.Get(
                    "应用设置已更新；任务栏播放状态仅支持 Windows 11。",
                    "App settings updated. Taskbar playback status is available only on Windows 11.")
                : AppText.Get("应用设置已更新。", "App settings updated."));
    }

    private bool BeginSettingsAction()
    {
        if (_isRunningSettingsAction)
        {
            return false;
        }

        _isRunningSettingsAction = true;
        SaveSettingsButton.IsEnabled = false;
        TestConnectionButton.IsEnabled = false;
        return true;
    }

    private void EndSettingsAction()
    {
        _isRunningSettingsAction = false;
        SaveSettingsButton.IsEnabled = true;
        TestConnectionButton.IsEnabled = true;
    }

    private void LoadSavedSecrets()
    {
        if (_tokenStore.TryLoad(out var savedToken))
        {
            _appToken = savedToken;
            TokenPasswordBox.Password = savedToken ?? string.Empty;
        }
        else
        {
            ConnectionStatusMenuItem.Text = AppText.Get(
                "无法读取已保存的 Token",
                "Could not read the saved token");
            ConnectionStatusIcon.Foreground = (Brush)RootGrid.Resources["DisconnectedBrush"];
        }

        if (_musixmatchKeyStore.TryLoad(out var savedKey))
        {
            _musixmatchApiKey = savedKey;
            MusixmatchApiKeyPasswordBox.Password = savedKey ?? string.Empty;
        }
    }

    /// <summary>
    /// The three value labels below only mirror their slider's position; the settings themselves
    /// are still applied by <see cref="SaveSettingsButton_Click"/>. They stay silent until the
    /// window is built because XAML raises ValueChanged while parsing.
    /// </summary>
    private void LyricFontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitialized)
        {
            LyricFontSizeValueText.Text = FormatFontSize(e.NewValue);
        }
    }

    private void BackgroundOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitialized)
        {
            BackgroundOpacityValueText.Text = FormatPercent(e.NewValue);
        }
    }

    private void BackgroundBlurSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitialized)
        {
            BackgroundBlurValueText.Text = FormatPercent(e.NewValue);
        }
    }

    private void LyricsSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLyricsTranslationAvailability();
    }

    private void UpdateLyricsTranslationAvailability()
    {
        var source = LyricsService.ParseSource(SelectedTag(LyricsSourceComboBox, "Auto"));
        var supportsTranslation = LyricsService.SupportsTranslation(source);
        LyricsTranslationToggle.IsEnabled = supportsTranslation;
        if (!supportsTranslation)
        {
            LyricsTranslationToggle.IsOn = false;
        }
    }

    private void LoadSettingsControls()
    {
        ApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
        TokenPasswordBox.Password = _appToken ?? string.Empty;
        SelectByTag(ThemeComboBox, _settings.Theme);
        SelectByTag(LanguageComboBox, _settings.Language);
        SelectByTag(DefaultPanelComboBox, _settings.DefaultPanel);
        var lyricsSource = LyricsService.ParseSource(_settings.LyricsSource);
        SelectByTag(LyricsSourceComboBox, lyricsSource.ToString());
        LyricFontSizeSlider.Value = _settings.LyricFontSize;
        AutoScrollToggle.IsOn = _settings.AutoScrollLyrics;
        ChineseLyricsToggle.IsOn = _settings.ConvertTraditionalLyricsToSimplified;
        _settings.ShowLyricsTranslation =
            LyricsService.SupportsTranslation(lyricsSource) && _settings.ShowLyricsTranslation;
        LyricsTranslationToggle.IsOn = _settings.ShowLyricsTranslation;
        UpdateLyricsTranslationAvailability();
        MusixmatchApiKeyPasswordBox.Password = _musixmatchApiKey ?? string.Empty;
        AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;
        TaskbarWidgetToggle.IsOn = _settings.TaskbarWidgetEnabled;
        TaskbarLyricsToggle.IsOn = _settings.ShowLyricsInTaskbar;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTrayOnClose;
        // The sliders work in whole percentages while the model keeps the 0–1 fraction, so
        // settings files written by earlier versions keep their original look.
        BackgroundOpacitySlider.Value = _settings.BackgroundOpacity * 100;
        BackgroundBlurSlider.Value = _settings.BackgroundBlur;
        LyricFontSizeValueText.Text = FormatFontSize(LyricFontSizeSlider.Value);
        BackgroundOpacityValueText.Text = FormatPercent(BackgroundOpacitySlider.Value);
        BackgroundBlurValueText.Text = FormatPercent(BackgroundBlurSlider.Value);
    }

    private async Task ShowSettingsDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppText.Get("确定", "OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
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
        _artworkPresenter.Apply(_settings.BackgroundOpacity, _settings.BackgroundBlur);
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
            RefreshLyricPlayback(forceScroll: true);
        }
    }

    private string DisplayLyricText(string text) =>
        _settings.ConvertTraditionalLyricsToSimplified
            ? ChineseTextConverter.ToSimplified(text)
            : text;

    private static string DisplayTranslationText(string text) =>
        ChineseTextConverter.ToSimplified(text);

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

    private static string? NormalizeArtworkUrl(string? url, int size = 800) =>
        url?
            .Replace("{w}", size.ToString(), StringComparison.Ordinal)
            .Replace("{h}", size.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);

    private static string FormatPercent(double percentage) =>
        $"{Math.Round(percentage, MidpointRounding.AwayFromZero)}%";

    private static string FormatFontSize(double size) => $"{size:0} px";

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
        _lyricTimer.Stop();
        CancelPlaybackSeekDebounce();
        _lyricsRefreshCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _taskbarWidgetHost.CommandRequested -= TaskbarWidgetHost_CommandRequested;
        _taskbarWidgetHost.Dispose();
        _nextTrackLyricsPreloader.Dispose();
        _artworkPresenter.Dispose();
        _trayIconHost.Dispose();
        _lifetimeCancellation.Dispose();
        _lyricsService.Dispose();
        _ciderService.Dispose();
    }
}
