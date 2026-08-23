using System.Text;

namespace FocusLock.Shared.Utilities;

public static class SettingsChallengeComparer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var normalized = value
            .Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    public static bool IsMatch(string? expected, string? actual) =>
        string.Equals(
            Normalize(expected),
            Normalize(actual),
            StringComparison.OrdinalIgnoreCase);

    public static int FirstDifference(string? expected, string? actual)
    {
        var left = Normalize(expected);
        var right = Normalize(actual);
        var common = Math.Min(left.Length, right.Length);

        for (var i = 0; i < common; i++)
        {
            if (!CharsEqual(left[i], right[i]))
                return i;
        }

        return left.Length == right.Length ? -1 : common;
    }

    public static bool CharsEqual(char expected, char actual) =>
        char.ToUpperInvariant(expected) == char.ToUpperInvariant(actual);
}
