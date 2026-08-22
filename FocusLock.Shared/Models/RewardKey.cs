namespace FocusLock.Shared.Models;

public sealed class RewardKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Signature { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public int RewardSeconds { get; set; }
    public DateTime? RedeemedUtc { get; set; }
    public bool Revoked { get; set; }

    public bool IsRedeemed => RedeemedUtc.HasValue;
    public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
    public string Status => Revoked ? "Đã thu hồi" : IsRedeemed ? "Đã dùng" : IsExpired ? "Hết hạn" : "Có thể dùng";
    public string RewardLabel => $"+{FormatDuration(RewardSeconds)}";
    public string ExpiresLocalLabel => ExpiresUtc <= DateTime.MinValue.AddYears(1)
        ? "Không rõ thời hạn"
        : $"Hết hạn {ExpiresUtc.ToLocalTime():dd/MM HH:mm:ss}";

    public string RemainingLabel
    {
        get
        {
            if (Revoked) return "Đã thu hồi";
            if (IsRedeemed) return RedeemedUtc is DateTime r ? $"Đã dùng {r.ToLocalTime():dd/MM HH:mm}" : "Đã dùng";
            var remaining = ExpiresUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return "Đã hết hạn";
            if (remaining.TotalHours >= 24) return $"Còn {(int)remaining.TotalDays} ngày {remaining.Hours} giờ";
            if (remaining.TotalHours >= 1) return $"Còn {(int)remaining.TotalHours} giờ {remaining.Minutes} phút";
            if (remaining.TotalMinutes >= 1) return $"Còn {(int)remaining.TotalMinutes} phút {remaining.Seconds} giây";
            return $"Còn {Math.Max(1, remaining.Seconds)} giây";
        }
    }

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
