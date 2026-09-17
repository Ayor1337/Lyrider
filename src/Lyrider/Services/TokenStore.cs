using System.Security.Cryptography;
using System.Text;

namespace Lyrider.Services;

public sealed class TokenStore
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("Lyrider.CiderToken.v1");
    private readonly string _tokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lyrider",
        "cider-token.dat");

    public bool TryLoad(out string? token)
    {
        token = null;

        try
        {
            if (!File.Exists(_tokenPath))
            {
                return true;
            }

            var encryptedBytes = File.ReadAllBytes(_tokenPath);
            var tokenBytes = ProtectedData.Unprotect(
                encryptedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);

            try
            {
                token = Encoding.UTF8.GetString(tokenBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySave(string? token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }

                return true;
            }

            var tokenBytes = Encoding.UTF8.GetBytes(token);

            try
            {
                var encryptedBytes = ProtectedData.Protect(
                    tokenBytes,
                    OptionalEntropy,
                    DataProtectionScope.CurrentUser);

                var directory = Path.GetDirectoryName(_tokenPath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(_tokenPath, encryptedBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
