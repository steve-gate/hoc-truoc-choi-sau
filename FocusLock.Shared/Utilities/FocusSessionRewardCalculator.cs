using FocusLock.Shared.Models;

namespace FocusLock.Shared.Utilities;

public static class FocusSessionRewardCalculator
{
    public static int ResolveFocusMinutes(BlockProfile? profile, UserSettings settings) =>
        profile is { Enabled: true, CustomRewardEnabled: true }
            ? Math.Max(1, profile.RewardFocusMinutes)
            : Math.Max(1, settings.FocusMinutesPerKey);

    public static int ResolveRewardMinutes(BlockProfile? profile, UserSettings settings) =>
        profile is { Enabled: true, CustomRewardEnabled: true }
            ? Math.Max(1, profile.RewardMinutes)
            : Math.Max(1, settings.RewardMinutesPerKey);

    public static int CalculateRewardSeconds(
        int sessionMinutes,
        UserSettings settings,
        BlockProfile? profile = null)
    {
        var minutes = Math.Clamp(sessionMinutes, 1, 1440);
        var focusMinutes = ResolveFocusMinutes(profile, settings);
        var rewardMinutesPerKey = ResolveRewardMinutes(profile, settings);

        var proportionalMinutes = (int)Math.Round(
            minutes * rewardMinutesPerKey / (double)focusMinutes,
            MidpointRounding.AwayFromZero);

        return Math.Clamp(Math.Max(1, proportionalMinutes) * 60, 60, 24 * 60 * 60);
    }
}
