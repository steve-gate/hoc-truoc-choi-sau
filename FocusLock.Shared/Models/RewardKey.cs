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
    public string RewardLabel => $"+{TimeSpan.FromSeconds(RewardSeconds):mm\\:ss}";
}
