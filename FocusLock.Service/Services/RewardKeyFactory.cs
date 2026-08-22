using System.Security.Cryptography;
using FocusLock.Shared.Models;

namespace FocusLock.Service.Services;

public static class RewardKeyFactory
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static RewardKey Create(int rewardSeconds, int expiryMinutes, IEnumerable<RewardKey> existing, SecureStateStore store)
    {
        var used = existing.Select(k => k.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string code;
        do { code = GenerateCode(); } while (used.Contains(code));

        var key = new RewardKey
        {
            Code = code,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(UserSettings.MinimumKeyExpiryMinutes, expiryMinutes)),
            RewardSeconds = rewardSeconds
        };
        key.Signature = store.SignKey(key);
        return key;
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[14];
        var p = 0;
        for (var i = 0; i < 12; i++)
        {
            if (i is 4 or 8) chars[p++] = '-';
            chars[p++] = Alphabet[bytes[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}
