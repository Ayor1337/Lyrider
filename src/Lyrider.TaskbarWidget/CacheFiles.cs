using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lyrider.TaskbarWidget;

/// <summary>
/// 歌词与封面磁盘缓存共用的文件规则：文件名是键的 SHA-256 十六进制串加扩展名。
/// 统计和清除只认这种文件名，用户把缓存位置指向已有其他文件的文件夹也不会误删。
/// </summary>
public static class CacheFiles
{
    private const int HashLength = 64;

    public static string FileName(string key, string extension) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + extension;

    public static FileInfo[] Enumerate(string? directoryPath, string extension)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(directoryPath) ? null : new DirectoryInfo(directoryPath);
            return directory is { Exists: true }
                ? directory.EnumerateFiles("*" + extension).Where(file => IsCacheFileName(file.Name, extension)).ToArray()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static Task<long> GetSizeAsync(string? directoryPath, string extension) =>
        Task.Run(() => Enumerate(directoryPath, extension).Sum(file =>
        {
            try
            {
                return file.Length;
            }
            catch (Exception)
            {
                return 0L;
            }
        }));

    /// <summary>返回没能删掉的文件数（例如被占用）。</summary>
    public static Task<int> ClearAsync(string? directoryPath, string extension) =>
        Task.Run(() => Enumerate(directoryPath, extension).Count(file => !TryDelete(file.FullName)));

    /// <summary>先写临时文件再替换，进程中途退出不会留下半个文件。失败只是少一条缓存，不向上抛。</summary>
    public static async Task WriteAtomicallyAsync(string path, Func<Stream, Task> write)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await write(stream);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(temporaryPath);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsCacheFileName(string name, string extension) =>
        name.Length == HashLength + extension.Length &&
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
        !name.AsSpan(0, HashLength).ContainsAnyExcept("0123456789abcdef");
}
