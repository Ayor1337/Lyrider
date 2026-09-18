using System.Collections.ObjectModel;
using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly List<TextBlock> _lyricTextBlocks = [];
    private readonly TaskbarWidgetHost _taskbarWidgetHost = new();
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
    private bool _isAutoScrollingLyrics;
    private bool _lyricsAreTimeSynced;
    private bool _isInitialized;
    private bool _isRunningTaskbarCommand;
    private bool _isExitRequested;
    private int _currentLyricIndex = -1;
    private int _refreshCount;
    private DateTimeOffset _lastManualLyricsScroll = DateTimeOffset.MinValue;

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
        LyricsPanel.Padding = new Thickness(0, 0, 0, 56);
        SettingsPageGrid.Padding = new Thickness(padding, 16, padding, 24);

        foreach (var row in new[] { ThemeSettingsRow, BackgroundSettingsRow, LyricFontSettingsRow,
            AutoScrollSettingsRow, DefaultPanelSettingsRow, VolumeSettingsRow, AlwaysOnTopSettingsRow,
            TaskbarWidgetSettingsRow, MinimizeToTraySettingsRow })
        {
            if (row.ColumnDefinitions.Count == 0)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var stacked = RootGrid.ActualWidth < 760;
            row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 260);
            Grid.SetColumn((FrameworkElement)row.Children[1], stacked ? 0 : 1);
            Grid.SetRow((FrameworkElement)row.Children[1], stacked ? 1 : 0);
        }
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
            UpdateTaskbarWidget(result.State == CiderConnectionState.Connected ? result.Track : null, playbackStatus);

            if (result.State != CiderConnectionState.Connected || result.Track is null)
            {
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
                _lyricsAreTimeSynced = track.HasTimeSyncedLyrics;
                await RefreshTrackDetailsAsync(track);
            }
            else
            {
                UpdateCurrentLyric(track.CurrentPlaybackTime);
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
        var lyricsTask = _ciderService.GetLyricsAsync(
            trackId,
            _appToken,
            _lifetimeCancellation.Token);

        await Task.WhenAll(queueTask, lyricsTask);
        ApplyQueue(await queueTask);
        _lyrics = await lyricsTask;
        RenderLyrics();
        UpdateCurrentLyric(track.CurrentPlaybackTime, forceScroll: true);
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
            RenderLyrics();
            return;
        }

        var durationSeconds = Math.Max(0, track.DurationInMillis / 1000);
        var currentSeconds = Math.Clamp(track.CurrentPlaybackTime, 0, durationSeconds);
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
            true));
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

    private void RenderLyrics()
    {
        LyricsStackPanel.Children.Clear();
        _lyricTextBlocks.Clear();
        _currentLyricIndex = -1;
        LyricsEmptyText.Visibility = _lyrics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LyricsStackPanel.Children.Add(new Border { Height = 220 });
        foreach (var line in _lyrics)
        {
            var textBlock = new TextBlock
            {
                Text = line.Text,
                FontSize = _settings.LyricFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                Opacity = _lyricsAreTimeSynced ? 0.36 : 0.72,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsTextSelectionEnabled = true
            };
            var lineIndex = _lyricTextBlocks.Count;
            textBlock.Tag = lineIndex;
            textBlock.PointerPressed += LyricTextBlock_PointerPressed;
            _lyricTextBlocks.Add(textBlock);
            LyricsStackPanel.Children.Add(textBlock);
        }

        LyricsStackPanel.Children.Add(new Border { Height = 220 });
    }

    private void UpdateCurrentLyric(double playbackTime, bool forceScroll = false)
    {
        if (!_lyricsAreTimeSynced || _lyrics.Count == 0)
        {
            return;
        }

        var nextIndex = -1;
        for (var index = 0; index < _lyrics.Count; index++)
        {
            if (_lyrics[index].StartTime <= playbackTime)
            {
                nextIndex = index;
            }
            else
            {
                break;
            }
        }

        if (nextIndex < 0 || (nextIndex == _currentLyricIndex && !forceScroll))
        {
            return;
        }

        _currentLyricIndex = nextIndex;
        for (var index = 0; index < _lyricTextBlocks.Count; index++)
        {
            var distance = Math.Abs(index - nextIndex);
            _lyricTextBlocks[index].Opacity = distance switch
            {
                0 => 1,
                1 => 0.58,
                _ => 0.32
            };
        }

        if (_settings.AutoScrollLyrics &&
            (forceScroll || DateTimeOffset.Now - _lastManualLyricsScroll > TimeSpan.FromSeconds(4)))
        {
            ScrollToCurrentLyric();
        }
    }

    private void ScrollToCurrentLyric()
    {
        if (_currentLyricIndex < 0 || _currentLyricIndex >= _lyricTextBlocks.Count)
        {
            return;
        }

        var currentLine = _lyricTextBlocks[_currentLyricIndex];
        var transform = currentLine.TransformToVisual(LyricsStackPanel);
        var point = transform.TransformPoint(new Point(0, 0));
        var targetOffset = Math.Max(0, point.Y - LyricsScrollViewer.ViewportHeight * 0.42);
        _isAutoScrollingLyrics = true;
        LyricsScrollViewer.ChangeView(null, targetOffset, null, false);
        _isAutoScrollingLyrics = false;
    }

    private async void LyricTextBlock_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_lyricsAreTimeSynced || sender is not TextBlock { Tag: int index } || index >= _lyrics.Count)
        {
            return;
        }

        await _ciderService.SeekAsync(
            _lyrics[index].StartTime,
            _appToken,
            _lifetimeCancellation.Token);
        await RefreshAsync();
    }

    private void LyricsScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isAutoScrollingLyrics && e.IsIntermediate)
        {
            _lastManualLyricsScroll = DateTimeOffset.Now;
        }
    }

    private void QueueViewButton_Click(object sender, RoutedEventArgs e) => ShowPanel("Queue");

    private void LyricsViewButton_Click(object sender, RoutedEventArgs e) => ShowPanel("Lyrics");

    private void MenuQueueItem_Click(object sender, RoutedEventArgs e)
    {
        ShowPlayerPanel("Queue");
    }

    private void MenuLyricsItem_Click(object sender, RoutedEventArgs e)
    {
        ShowPlayerPanel("Lyrics");
    }

    private void MenuSettingsItem_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsControls();
        PlayerPageGrid.Visibility = Visibility.Collapsed;
        SettingsPageGrid.Visibility = Visibility.Visible;
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

    private void ShowPlayerPanel(string panel)
    {
        SettingsPageGrid.Visibility = Visibility.Collapsed;
        PlayerPageGrid.Visibility = Visibility.Visible;
        ShowPanel(panel);
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
            ScrollToCurrentLyric();
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
            ConnectionStatusMenuItem.Text = "Cider 未接受播放指令";
            ConnectionStatusIcon.Foreground = (Brush)RootGrid.Resources["DisconnectedBrush"];
            return;
        }

        await RefreshAsync(forceDetails: true);
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

    private void BackToPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPageGrid.Visibility = Visibility.Collapsed;
        PlayerPageGrid.Visibility = Visibility.Visible;
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Text = "正在测试连接…";
        if (!Uri.TryCreate(ApiBaseUrlTextBox.Text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            SettingsStatusText.Text = "API 地址必须是有效的 HTTP 或 HTTPS 地址";
            return;
        }

        using var service = new CiderService(ApiBaseUrlTextBox.Text);
        var result = await service.GetNowPlayingAsync(
            NormalizeToken(TokenPasswordBox.Password),
            _lifetimeCancellation.Token);
        SettingsStatusText.Text = result.State == CiderConnectionState.Connected
            ? "连接成功"
            : result.Message;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ciderService.TryUpdateBaseAddress(ApiBaseUrlTextBox.Text))
        {
            SettingsStatusText.Text = "API 地址必须是有效的 HTTP 或 HTTPS 地址";
            return;
        }

        var token = NormalizeToken(TokenPasswordBox.Password);
        if (!_tokenStore.TrySave(token))
        {
            SettingsStatusText.Text = "无法保存 Token";
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
            SettingsStatusText.Text = "无法保存应用设置";
            return;
        }

        _appToken = token;
        ApplySettings();
        SettingsStatusText.Text = _settings.TaskbarWidgetEnabled && !_taskbarWidgetHost.IsSupported
            ? "设置已保存；任务栏播放状态仅支持 Windows 11"
            : "设置已保存";
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
        SettingsStatusText.Text = string.Empty;
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

        RenderLyrics();
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
        _ciderService.Dispose();
    }
}
