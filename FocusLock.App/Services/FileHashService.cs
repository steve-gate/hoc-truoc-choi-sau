using System.Security.Cryptography;
using System.IO;

namespace FocusLock.App.Services;

public static class FileHashService
{
    public static string TrySha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }
}
