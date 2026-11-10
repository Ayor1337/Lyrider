using System.Text.Json;
using System.Text.Json.Serialization;
using Lyrider.Models;
using Lyrider.TaskbarWidget;

namespace Lyrider.Services;

/// <summary>
/// 满意歌词结果的磁盘缓存，跨重启保留，不自动过期。每个缓存键一个 JSON 文件，命名与清除规则见 <see cref="CacheFiles"/>。
/// </summary>
public sealed class LyricsDiskCache
{
    private const int FormatVersion = 1;
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public LyricsDiskCache(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    /// <summary>缓存根目录的默认位置；封面放在它下面的 <see cref="ArtworkCache.DirectoryName"/> 子目录。</summary>
    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lyrider", "lyrics-data");

    public string DirectoryPath { get; }

    /// <summary>空值表示使用默认位置。</summary>
    public static string ResolveDirectoryPath(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? DefaultDirectoryPath : configuredPath;

    public async Task<LyricsSnapshot?> TryLoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<Entry>(stream, JsonOptions, cancellationToken);
            return entry is { Version: FormatVersion, Snapshot.Lines.Count: > 0 } &&
                string.Equals(entry.Key, key, StringComparison.Ordinal)
                ? entry.Snapshot
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task SaveAsync(string key, LyricsSnapshot snapshot) =>
        CacheFiles.WriteAtomicallyAsync(
            PathFor(key),
            stream => JsonSerializer.SerializeAsync(stream, new Entry(FormatVersion, key, snapshot), JsonOptions));

    public Task<long> GetSizeAsync() => CacheFiles.GetSizeAsync(DirectoryPath, Extension);

    /// <summary>返回没能删掉的文件数（例如被占用）。</summary>
    public Task<int> ClearAsync() => CacheFiles.ClearAsync(DirectoryPath, Extension);

    private string PathFor(string key) => Path.Combine(DirectoryPath, FileNameFor(key));

    // 格式版本参与哈希，格式变了旧文件自然不再命中。
    internal static string FileNameFor(string key) => CacheFiles.FileName($"v{FormatVersion}|{key}", Extension);

    private sealed record Entry(int Version, string Key, LyricsSnapshot Snapshot);
}
