using System.Security.Cryptography;
using System.Text;

namespace Lyrider.Services;

public sealed class TokenStore
{
    private readonly ProtectedSecretStore _store = new(
        "cider-token.dat",
        "Lyrider.CiderToken.v1");

    public bool TryLoad(out string? token) => _store.TryLoad(out token);

    public bool TrySave(string? token) => _store.TrySave(token);
}

public sealed class MusixmatchKeyStore
{
    private readonly ProtectedSecretStore _store = new(
        "musixmatch-api-key.dat",
        "Lyrider.MusixmatchApiKey.v1");

    public bool TryLoad(out string? key) => _store.TryLoad(out key);

    public bool TrySave(string? key) => _store.TrySave(key);
}

internal sealed class ProtectedSecretStore(string fileName, string entropyPurpose)
{
    private readonly byte[] _optionalEntropy = Encoding.UTF8.GetBytes(entropyPurpose);
    private readonly string _secretPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lyrider",
        fileName);

    public bool TryLoad(out string? secret)
    {
        secret = null;

        try
        {
            if (!File.Exists(_secretPath))
            {
                return true;
            }

            var encryptedBytes = File.ReadAllBytes(_secretPath);
            var secretBytes = ProtectedData.Unprotect(
                encryptedBytes,
                _optionalEntropy,
                DataProtectionScope.CurrentUser);

            try
            {
                secret = Encoding.UTF8.GetString(secretBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySave(string? secret)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                if (File.Exists(_secretPath))
                {
                    File.Delete(_secretPath);
                }

                return true;
            }

            var secretBytes = Encoding.UTF8.GetBytes(secret);

            try
            {
                var encryptedBytes = ProtectedData.Protect(
                    secretBytes,
                    _optionalEntropy,
                    DataProtectionScope.CurrentUser);

                var directory = Path.GetDirectoryName(_secretPath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(_secretPath, encryptedBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
