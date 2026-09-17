using Lyrider.Models;
using Lyrider.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lyrider;

public sealed partial class MainWindow : Window
{
    private readonly CiderService _ciderService = new();
    private readonly TokenStore _tokenStore = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private string? _appToken;
    private string? _artworkUrl;
    private bool _isRefreshing;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_tokenStore.TryLoad(out var savedToken))
        {
            _appToken = savedToken;
            TokenPasswordBox.Password = savedToken ?? string.Empty;
        }
        else
        {
            ConnectionStatusText.Text = "无法读取已保存的 Token";
            ConnectionIndicator.Fill = (Brush)RootGrid.Resources["DisconnectedBrush"];
        }

        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await RefreshAsync();
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        await RefreshAsync();
    }

    private async void ApplyTokenButton_Click(object sender, RoutedEventArgs e)
    {
        _appToken = string.IsNullOrWhiteSpace(TokenPasswordBox.Password)
            ? null
            : TokenPasswordBox.Password;

        if (!_tokenStore.TrySave(_appToken))
        {
            ConnectionStatusText.Text = "无法保存 Token";
            ConnectionIndicator.Fill = (Brush)RootGrid.Resources["DisconnectedBrush"];
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            var result = await _ciderService.GetNowPlayingAsync(
                _appToken,
                _lifetimeCancellation.Token);

            UpdateConnectionState(result);

            if (result.State == CiderConnectionState.Connected)
            {
                UpdateNowPlaying(result.Track);
            }
            else
            {
                UpdateNowPlaying(null);
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

    private void UpdateConnectionState(CiderResult result)
    {
        ConnectionStatusText.Text = result.Message;
        ConnectionIndicator.Fill = result.State == CiderConnectionState.Connected
            ? (Brush)RootGrid.Resources["ConnectedBrush"]
            : (Brush)RootGrid.Resources["DisconnectedBrush"];
    }

    private void UpdateNowPlaying(NowPlayingInfo? track)
    {
        if (track is null)
        {
            SongNameText.Text = "未在播放";
            ArtistNameText.Text = "—";
            AlbumNameText.Text = "—";
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            PlaybackProgressBar.Maximum = 1;
            PlaybackProgressBar.Value = 0;
            SetArtwork(null);
            return;
        }

        var durationSeconds = Math.Max(0, track.DurationInMillis / 1000);
        var currentSeconds = Math.Clamp(track.CurrentPlaybackTime, 0, durationSeconds);

        SongNameText.Text = ValueOrFallback(track.Name);
        ArtistNameText.Text = ValueOrFallback(track.ArtistName);
        AlbumNameText.Text = ValueOrFallback(track.AlbumName);
        CurrentTimeText.Text = FormatTime(currentSeconds);
        TotalTimeText.Text = FormatTime(durationSeconds);
        PlaybackProgressBar.Maximum = Math.Max(1, durationSeconds);
        PlaybackProgressBar.Value = currentSeconds;
        SetArtwork(NormalizeArtworkUrl(track.Artwork?.Url));
    }

    private void SetArtwork(string? url)
    {
        if (string.Equals(_artworkUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _artworkUrl = url;
        ArtworkImage.Source = Uri.TryCreate(url, UriKind.Absolute, out var artworkUri)
            ? new BitmapImage(artworkUri)
            : null;
    }

    private static string? NormalizeArtworkUrl(string? url)
    {
        return url?
            .Replace("{w}", "600", StringComparison.Ordinal)
            .Replace("{h}", "600", StringComparison.Ordinal);
    }

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
        _refreshTimer.Stop();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _ciderService.Dispose();
    }
}
