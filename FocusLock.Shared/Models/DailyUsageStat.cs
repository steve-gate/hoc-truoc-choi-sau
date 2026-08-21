namespace FocusLock.Shared.Models;

public sealed class DailyUsageStat
{
    // Ngày theo múi giờ cục bộ của máy Windows, định dạng yyyy-MM-dd.
    public string DateKey { get; set; } = "";
    public long FocusSeconds { get; set; }
    public long EntertainmentSeconds { get; set; }
    public long SuspiciousSeconds { get; set; }
    public int KeysGenerated { get; set; }
    public int KeysRedeemed { get; set; }
    public long RewardSecondsGranted { get; set; }
}
