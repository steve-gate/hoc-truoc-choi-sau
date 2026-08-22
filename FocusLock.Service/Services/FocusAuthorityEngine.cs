using System.Diagnostics;
using System.Text.Json;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;

namespace FocusLock.Service.Services;

public sealed class FocusAuthorityEngine
{
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "focuslock", "focuslock.service", "focuslock.nativehost"
    };
    private readonly object _gate = new();
    private readonly SecureStateStore _store;
    private readonly Queue<DateTime> _activityEvents = new();
    private readonly Queue<DateTime> _browserActivityEvents = new();
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc, string Hash)> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (long StartTicks, string AppId)> _suspendedProcesses = new();
    private AppState _state;
    private string _agentInstanceId = "";
    private long _lastSequence;
    private long _lastHeartbeatTick;
    private long _lastActivityTick;
    private string _lastFocusAppId = "";
    private string _currentMode = "Đang chờ agent";
    private string _currentApp = "—";
    private bool _isIdle;
    private bool _inputMonitorHealthy;
    private UsageSession? _openSession;
    private BrowserContextSample? _browserContext;
    private long _lastBrowserContextTick;
    private string _currentBrowserCategory = "Neutral";
    private string _currentBrowserRule = "—";
    private bool _currentBrowserBlocked;
    private string _currentBrowserProfile = "—";
    private string _currentBrowserAccess = "—";
    private int _currentBrowserAllowanceRemainingSeconds;
    private long _lastBrowserInteractionCounter;
    private string _lastBrowserInteractionUrl = "";
    private bool _browserMediaQualified;
    private DateTime _browserEngagedUntilUtc = DateTime.MinValue;
    private long _lastBrowserAccountingTick;
    private string _lastBrowserAccountingRuleId = "";
    private double _browserAccountingCarrySeconds;
    private string _lastVerifiedForegroundProcess = "";
    private long _lastVerifiedForegroundTick;

    // V7.5.1: browser entertainment is debited by the Guard's 1-second monotonic
    // tick instead of relying on MV3 background timers/window.focused.
    private long _lastBrowserEntertainmentGuardTick;
    private double _browserEntertainmentGuardCarrySeconds;
    private string _lastBrowserEntertainmentGuardRuleId = "";

    // V7.4: bubble/session state is explicit instead of parsing CurrentMode.
    private bool _entertainmentSessionActive;
    private EntertainmentAccess _currentEntertainmentAccess = EntertainmentAccess.Blocked;
    private string _currentEntertainmentProfile = "—";
    private int _currentEntertainmentAllowanceRemainingSeconds;

    public FocusAuthorityEngine(SecureStateStore store)
    {
        _store = store;
        _state = store.Load();
        NormalizeState();
        AddAudit("Service", "FocusLock Guard V7.5.6 khởi động · NativeHost Win32 foreground accounting cho website.");
    }

    public PipeResponse Handle(PipeRequest request)
    {
        lock (_gate)
        {
            try
            {
                BrowserDecision? browserDecision = null;
                string message;
                var command = request.Command.ToLowerInvariant();
                if (IsConfigurationMutationCommand(command)) EnsureConfigurationChangeAllowedUnsafe();
                switch (command)
                {
                    case "activity": message = HandleActivityCommand(request.Activity); break;
                    case "addapp": message = AddApp(request.App); break;
                    case "removeapp": message = RemoveApp(request.AppId); break;
                    case "toggleapp": message = ToggleApp(request.AppId); break;
                    case "cycleapplock": message = CycleAppBlockAction(request.AppId); break;
                    case "cycleappprofile": message = CycleAppProfile(request.AppId); break;
                    case "setappprofile": message = SetAppProfile(request.AppId, request.BlockProfileId); break;
                    case "setappblockaction": message = SetAppBlockAction(request.AppId, request.UseCustomBlockAction, request.BlockAction); break;
                    case "addblockprofile": message = AddBlockProfile(request.BlockProfile); break;
                    case "toggleblockprofile": message = ToggleBlockProfile(request.BlockProfileId); break;
                    case "removeblockprofile": message = RemoveBlockProfile(request.BlockProfileId); break;
                    case "updateblockprofile": message = UpdateBlockProfile(request.BlockProfile); break;
                    case "enablestrict": message = EnableStrictMode(request.DurationMinutes); break;
                    case "requeststrictunlock": message = RequestStrictUnlock(); break;
                    case "disablestrict": message = DisableStrictMode(); break;
                    case "startlockedsession": message = StartLockedSession(request.DurationMinutes); break;
                    case "startwhitelistsession": message = StartWhitelistSession(request.DurationMinutes); break;
                    case "enablesettingstextprotection": message = EnableSettingsTextProtection(); break;
                    case "unlocksettingstextprotection": message = UnlockSettingsTextProtection(request.TextValue); break;
                    case "enablesettingstimeprotection": message = EnableSettingsTimeProtection(request.StartUtc, request.UntilUtc); break;
                    case "settings": message = UpdateSettings(request.Settings); break;
                    case "redeem": message = Redeem(request.KeyCode); break;
                    case "importlegacy": message = ImportLegacy(request.LegacyState); break;
                    case "browsercontext":
                        browserDecision = HandleBrowserContext(request.BrowserContext);
                        message = browserDecision.Message;
                        break;
                    case "addbrowserrule": message = AddBrowserRule(request.BrowserRule); break;
                    case "removebrowserrule": message = RemoveBrowserRule(request.BrowserRuleId); break;
                    case "togglebrowserrule": message = ToggleBrowserRule(request.BrowserRuleId); break;
                    case "cyclebrowserprofile": message = CycleBrowserProfile(request.BrowserRuleId); break;
                    case "setbrowserprofile": message = SetBrowserProfile(request.BrowserRuleId, request.BlockProfileId); break;
                    case "snapshot": message = "OK"; break;
                    default: throw new InvalidOperationException("Lệnh không được hỗ trợ.");
                }

                return new PipeResponse
                {
                    Id = request.Id,
                    Ok = true,
                    Message = message,
                    Snapshot = BuildSnapshotUnsafe(),
                    BrowserDecision = browserDecision
                };
            }
            catch (Exception ex)
            {
                return new PipeResponse
                {
                    Id = request.Id,
                    Ok = false,
                    Message = ex.Message,
                    Snapshot = BuildSnapshotUnsafe()
                };
            }
        }
    }

    public void GuardTick()
    {
        lock (_gate)
        {
            CheckClock(DateTime.UtcNow);
            ResetDailyAllowancesUnsafe(DateTime.Now);
            if (!HeartbeatHealthyUnsafe())
            {
                var verifiedBrowserSession =
                    _browserContext is not null &&
                    BrowserBridgeHealthyUnsafe() &&
                    _browserContext.WindowFocused;

                if (!verifiedBrowserSession)
                {
                    _currentMode = "Agent mất kết nối – khóa an toàn";
                    _currentApp = "—";
                    EndUsageSessionUnsafe("Mất heartbeat");
                }
            }
            _state.LastSeenUtc = Max(_state.LastSeenUtc, DateTime.UtcNow);
        }
    }

    public void Save()
    {
        lock (_gate) _store.Save(_state);
    }

    public bool ShouldLockEntertainment()
    {
        lock (_gate)
        {
            return _state.ClockRollbackDetected || _state.EntertainmentBalanceSeconds <= 0 || !HeartbeatHealthyUnsafe();
        }
    }

    public void EnforceEntertainmentPolicies()
    {
        List<TrackedApp> targets;
        bool verifyHash;
        lock (_gate)
        {
            targets = _state.Apps
                .Where(a => a.Category == AppCategory.Entertainment && IsAppPolicyEnabledUnsafe(a))
                .Select(Clone)
                .ToList();
            verifyHash = _state.Settings.VerifyExecutableHash;
        }
        if (targets.Count == 0)
        {
            ReleaseEntertainmentLock();
            return;
        }

        var targetHashes = targets.Where(t => !string.IsNullOrWhiteSpace(t.Sha256))
            .Select(t => t.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenSuspended = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = ProcessTools.TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;
                var processName = process.ProcessName;

                var matched = targets.FirstOrDefault(t =>
                    string.Equals(t.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(t.ExePath) || PathsEqual(t.ExePath, path)));

                if (matched is null && verifyHash && targetHashes.Count > 0)
                {
                    var hash = GetCachedHash(path);
                    if (!string.IsNullOrWhiteSpace(hash))
                        matched = targets.FirstOrDefault(t => string.Equals(t.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                }

                if (matched is null) continue;

                bool shouldLock;
                string reason;
                lock (_gate) shouldLock = ShouldLockEntertainmentAppUnsafe(matched, out reason);
                if (!shouldLock)
                {
                    ResumeIfTrackedUnsafe(process);
                    continue;
                }

                EntertainmentBlockAction effectiveAction;
                lock (_gate) effectiveAction = GetEffectiveBlockActionUnsafe(matched);
                switch (effectiveAction)
                {
                    case EntertainmentBlockAction.Suspend:
                        seenSuspended.Add(process.Id);
                        if (!SuspendOnceUnsafe(process, matched))
                        {
                            // Protected/anti-cheat processes may reject suspension. Fail closed.
                            seenSuspended.Remove(process.Id);
                            ProcessTools.TryKill(process);
                        }
                        break;
                    case EntertainmentBlockAction.BlockLaunch:
                    case EntertainmentBlockAction.Close:
                    default:
                        ResumeIfTrackedUnsafe(process);
                        ProcessTools.TryKill(process);
                        break;
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        ResumeStaleSuspendedUnsafe(seenSuspended);
    }

    // Compatibility with older GuardWorker builds/scripts.
    public void EnforceEntertainmentLock() => EnforceEntertainmentPolicies();

    public void ReleaseEntertainmentLock()
    {
        List<int> pids;
        lock (_gate) pids = _suspendedProcesses.Keys.ToList();
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                ResumeIfTrackedUnsafe(process);
            }
            catch
            {
                lock (_gate) _suspendedProcesses.Remove(pid);
            }
        }
    }

    private bool SuspendOnceUnsafe(Process process, TrackedApp app)
    {
        long startTicks;
        try { startTicks = process.StartTime.ToUniversalTime().Ticks; }
        catch { return false; }

        lock (_gate)
        {
            if (_suspendedProcesses.TryGetValue(process.Id, out var existing) && existing.StartTicks == startTicks)
                return true;
        }

        if (!ProcessTools.TrySuspend(process)) return false;
        lock (_gate)
        {
            _suspendedProcesses[process.Id] = (startTicks, app.Id);
            AddAudit("Block", $"Khóa tại chỗ {app.Name} (PID {process.Id}) vì hết thời gian giải trí.");
        }
        return true;
    }

    private void ResumeIfTrackedUnsafe(Process process)
    {
        (long StartTicks, string AppId) existing;
        lock (_gate)
        {
            if (!_suspendedProcesses.TryGetValue(process.Id, out existing)) return;
        }

        try
        {
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            if (startTicks == existing.StartTicks) ProcessTools.TryResume(process);
        }
        catch { }
        finally
        {
            lock (_gate) _suspendedProcesses.Remove(process.Id);
        }
    }

    private void ResumeStaleSuspendedUnsafe(HashSet<int> keep)
    {
        List<int> stale;
        lock (_gate) stale = _suspendedProcesses.Keys.Where(pid => !keep.Contains(pid)).ToList();
        foreach (var pid in stale)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                ResumeIfTrackedUnsafe(process);
            }
            catch
            {
                lock (_gate) _suspendedProcesses.Remove(pid);
            }
        }
    }

    private string HandleActivityCommand(ActivitySample? sample)
    {
        if (sample is null) throw new InvalidOperationException("Thiếu activity sample.");
        ApplyActivity(sample);
        return "Activity accepted";
    }

    private void ApplyActivity(ActivitySample sample)
    {
        var now = DateTime.UtcNow;
        CheckClock(now);

        if (!string.Equals(_agentInstanceId, sample.AgentInstanceId, StringComparison.Ordinal))
        {
            _agentInstanceId = sample.AgentInstanceId;
            _lastSequence = 0;
            _lastActivityTick = 0;
            _lastFocusAppId = "";
            AddAudit("Agent", "Agent phiên người dùng đã kết nối/restart.");
        }
        if (sample.Sequence <= _lastSequence) return;
        _lastSequence = sample.Sequence;

        var currentTick = Stopwatch.GetTimestamp();
        var gapSeconds = _lastActivityTick == 0 ? 0 : (currentTick - _lastActivityTick) / (double)Stopwatch.Frequency;
        _lastActivityTick = currentTick;
        _lastHeartbeatTick = currentTick;

        while (_activityEvents.Count > 0 && (now - _activityEvents.Peek()).TotalSeconds > 60) _activityEvents.Dequeue();

        _inputMonitorHealthy = sample.InputMonitorHealthy;
        _isIdle = sample.IdleMilliseconds >= Math.Max(1, _state.Settings.IdleThresholdSeconds) * 1000L;
        var actual = ValidateInteractiveProcess(sample);
        _lastVerifiedForegroundProcess = actual?.ProcessName ?? (sample.ProcessName ?? "");
        _lastVerifiedForegroundTick = currentTick;
        if (actual is null)
        {
            ClearEntertainmentContextUnsafe();
            _currentMode = _isIdle ? "Đang nghỉ" : "Ứng dụng không theo dõi";
            _currentApp = string.IsNullOrWhiteSpace(sample.ProcessName) ? "—" : sample.ProcessName;
            EndUsageSessionUnsafe("Rời ứng dụng theo dõi");
            return;
        }

        _currentApp = actual.Value.ProcessName;
        TrackedApp? tracked;
        if (IsSupportedBrowserProcess(actual.Value.ProcessName))
        {
            tracked = ResolveBrowserTrackedUnsafe(actual.Value.ProcessName, actual.Value.Path);
            if (tracked is null)
            {
                // Không cho phép khai báo chrome/msedge là Focus để kiếm thời gian khi extension bị tắt
                // hoặc website chưa có rule. Process-level Entertainment vẫn được giữ làm fallback an toàn.
                var processRule = FindTracked(actual.Value.ProcessName, actual.Value.Path);
                if (processRule?.Category == AppCategory.Entertainment)
                {
                    tracked = processRule;
                }
                else
                {
                    ClearEntertainmentContextUnsafe();
                    _currentMode = BrowserBridgeHealthyUnsafe() ? "Website chưa được phân loại" : "Browser Extension chưa kết nối";
                    _currentApp = BrowserBridgeHealthyUnsafe() && _browserContext is not null && !string.IsNullOrWhiteSpace(_browserContext.Host)
                        ? $"{BrowserDisplayName(_browserContext.Browser)} · {_browserContext.Host}"
                        : actual.Value.ProcessName;
                    EndUsageSessionUnsafe("Browser không có rule hợp lệ");
                    return;
                }
            }
        }
        else
        {
            tracked = FindTracked(actual.Value.ProcessName, actual.Value.Path);
        }

        if (tracked is null)
        {
            ClearEntertainmentContextUnsafe();
            _currentMode = _isIdle ? "Đang nghỉ" : "Ứng dụng không theo dõi";
            EndUsageSessionUnsafe("Ứng dụng không thuộc rule");
            return;
        }
        _currentApp = tracked.Name;
        var isBrowserTracked = tracked.Id.StartsWith("browser:", StringComparison.Ordinal);
        var isBrowserFocus = tracked.Category == AppCategory.Focus && isBrowserTracked;

        // V7.5.6: Browser timing is authoritative in HandleBrowserContext(), where
        // NativeHost verifies the actual Win32 foreground window in the user session.
        // The desktop agent must never credit/debit browser time, otherwise a stale
        // desktop foreground sample could double-charge or freeze browser accounting.
        if (isBrowserTracked)
        {
            if (tracked.Category == AppCategory.Focus)
                ClearEntertainmentContextUnsafe();
            return;
        }

        if (_state.ClockRollbackDetected)
        {
            ClearEntertainmentContextUnsafe();
            _currentMode = "Phát hiện thay đổi giờ hệ thống – đang khóa";
            EndUsageSessionUnsafe("Clock rollback");
            return;
        }

        var canCountOneSecond = gapSeconds > 0 && gapSeconds <= 2.5;
        if (tracked.Category == AppCategory.Focus)
        {
            ClearEntertainmentContextUnsafe();
            if (_lastFocusAppId != tracked.Id)
            {
                _lastFocusAppId = tracked.Id;
                _activityEvents.Clear();
            }
            if (sample.InputChanged) _activityEvents.Enqueue(now);
            while (_activityEvents.Count > 0 && (now - _activityEvents.Peek()).TotalSeconds > 60) _activityEvents.Dequeue();

            // Watching/listening to an actively progressing lesson is valid Focus even if
            // Windows reports no mouse/keyboard activity for a while.
            if (_isIdle && !(isBrowserFocus && BrowserFocusQualifiedUnsafe(now)))
            {
                _currentMode = "Focus tạm dừng (idle)";
                EndUsageSessionUnsafe("Focus idle");
                return;
            }

            if (!FocusActivityQualifies(isBrowserFocus))
            {
                _currentMode = "Focus tạm dừng (hoạt động quá thấp)";
                EndUsageSessionUnsafe("Activity score thấp");
                if (canCountOneSecond)
                {
                    _state.SuspiciousSeconds++;
                    GetDailyStatUnsafe(DateTime.Now.Date).SuspiciousSeconds++;
                }
                return;
            }

            _currentMode = isBrowserFocus && _browserMediaQualified
                ? "Đang học trên web (media đang phát)"
                : isBrowserFocus ? "Đang học trên web" : "Đang học / làm việc";
            if (canCountOneSecond) CreditFocusSecond(tracked);
            return;
        }

        _lastFocusAppId = "";
        var access = GetEntertainmentAccessUnsafe(tracked, out var accessReason);
        if (access == EntertainmentAccess.Blocked)
        {
            SetEntertainmentContextUnsafe(tracked, EntertainmentAccess.Blocked);
            _currentMode = $"Giải trí đang bị khóa · {accessReason}";
            EndUsageSessionUnsafe(accessReason);
            return;
        }

        SetEntertainmentContextUnsafe(tracked, access);
        if (_isIdle)
        {
            _currentMode = "Giải trí tạm dừng (idle)";
            EndUsageSessionUnsafe("Giải trí idle");
            return;
        }
        _currentMode = AccessModeLabel(access);
        if (canCountOneSecond)
        {
            ConsumeEntertainmentSecondUnsafe(tracked, access);
            _state.TotalEntertainmentSeconds++;
            RecordUsageSecondUnsafe(tracked, AppCategory.Entertainment);
            SetEntertainmentContextUnsafe(tracked, GetEntertainmentAccessUnsafe(tracked, out _));
        }
    }

    private bool FocusActivityQualifies(bool isBrowserFocus = false)
    {
        if (!_state.Settings.AntiCheatEnabled) return true;
        if (isBrowserFocus && (_browserMediaQualified || _browserEngagedUntilUtc > DateTime.UtcNow)) return true;
        var events = _activityEvents.Count + (isBrowserFocus ? _browserActivityEvents.Count : 0);
        return events >= Math.Max(1, _state.Settings.MinimumActivityEventsPerMinute);
    }

    private void CreditFocusSecond(TrackedApp tracked)
    {
        _state.FocusProgressSeconds++;
        _state.TotalFocusSeconds++;
        RecordUsageSecondUnsafe(tracked, AppCategory.Focus);
        var target = Math.Max(60, _state.Settings.FocusMinutesPerKey * 60);
        while (_state.FocusProgressSeconds >= target)
        {
            _state.FocusProgressSeconds -= target;
            var key = RewardKeyFactory.Create(
                Math.Max(60, _state.Settings.RewardMinutesPerKey * 60),
                Math.Max(UserSettings.MinimumKeyExpiryMinutes, _state.Settings.KeyExpiryMinutes),
                _state.Keys,
                _store);
            _state.Keys.Add(key);
            GetDailyStatUnsafe(DateTime.Now.Date).KeysGenerated++;
            AddAudit("Reward", $"Đã tạo key {key.Code}, thưởng {key.RewardSeconds / 60} phút.");
        }
    }

    private (string ProcessName, string Path)? ValidateInteractiveProcess(ActivitySample sample)
    {
        if (sample.ProcessId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(sample.ProcessId);
            if (process.HasExited) return null;
            var name = process.ProcessName;
            var path = ProcessTools.TryGetProcessPath(process) ?? "";
            if (!string.Equals(name, sample.ProcessName, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrWhiteSpace(sample.ExePath) && !string.IsNullOrWhiteSpace(path) && !PathsEqual(sample.ExePath, path)) return null;
            return (name, path);
        }
        catch { return null; }
    }

    private BrowserDecision HandleBrowserContext(BrowserContextSample? sample)
    {
        if (sample is null) throw new InvalidOperationException("Thiếu browser context.");

        sample.Browser = NormalizeBrowserName(sample.Browser);
        sample.Url = TrimTo(sample.Url, 8192);
        sample.Title = TrimTo(sample.Title, 1024);
        sample.Host = ExtractHost(sample.Url);
        sample.ObservedUtc = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        var browserForegroundVerified = sample.WindowFocused || BrowserForegroundActiveUnsafe(sample.Browser);
        while (_browserActivityEvents.Count > 0 && (now - _browserActivityEvents.Peek()).TotalSeconds > 60)
            _browserActivityEvents.Dequeue();

        if (!string.Equals(_lastBrowserInteractionUrl, sample.Url, StringComparison.Ordinal) ||
            sample.InteractionCounter < _lastBrowserInteractionCounter)
        {
            _lastBrowserInteractionUrl = sample.Url;
            _lastBrowserInteractionCounter = sample.InteractionCounter;
            _browserActivityEvents.Clear();
            _lastBrowserAccountingTick = 0;
            _lastBrowserAccountingRuleId = "";
            _browserAccountingCarrySeconds = 0;
        }
        else if (sample.InteractionCounter > _lastBrowserInteractionCounter)
        {
            var delta = Math.Clamp(sample.InteractionCounter - _lastBrowserInteractionCounter, 1, 10);
            for (var i = 0L; i < delta; i++) _browserActivityEvents.Enqueue(now);
            _lastBrowserInteractionCounter = sample.InteractionCounter;
        }

        // Reading is valid work too. One trusted click/key/scroll grants a
        // short engagement lease; actively progressing media qualifies itself.
        if (sample.LastUserActivityUnixMs > 0)
        {
            try
            {
                var lastUserUtc = DateTimeOffset.FromUnixTimeMilliseconds(sample.LastUserActivityUnixMs).UtcDateTime;
                if (lastUserUtc <= now.AddSeconds(5) && now - lastUserUtc <= TimeSpan.FromMinutes(5))
                {
                    var leaseUntil = lastUserUtc.AddSeconds(90);
                    if (leaseUntil > _browserEngagedUntilUtc) _browserEngagedUntilUtc = leaseUntil;
                }
            }
            catch { }
        }

        _browserMediaQualified = browserForegroundVerified && sample.DocumentVisible &&
                                 sample.MediaPlaying && sample.MediaProgressing;
        if (_browserMediaQualified) _browserEngagedUntilUtc = now.AddSeconds(5);

        _browserContext = sample;
        _lastBrowserContextTick = Stopwatch.GetTimestamp();

        // Matching is independent from Block Profile enabled state.
        var rule = _state.Settings.BrowserRulesEnabled ? FindBrowserRuleUnsafe(sample.Url, sample.Title) : null;
        _currentBrowserCategory = rule is null ? "Neutral" : rule.Category == AppCategory.Focus ? "Focus" : "Giải trí";
        _currentBrowserRule = rule?.DisplayName ?? "—";

        var browserFocusOk = rule?.Category == AppCategory.Focus &&
                             browserForegroundVerified &&
                             sample.DocumentVisible &&
                             BrowserFocusQualifiedUnsafe(now);

        string browserBlockReason = "";
        EntertainmentAccess access = EntertainmentAccess.Free;

        if (IsWhitelistSessionActiveUnsafe())
        {
            _currentBrowserBlocked = rule?.Category != AppCategory.Focus;
            if (_currentBrowserBlocked) browserBlockReason = "Focus-only đang bật";
            if (rule?.Category == AppCategory.Entertainment && browserForegroundVerified)
            {
                var policyApp = BrowserRuleAsTrackedAppUnsafe(rule, sample);
                var profile = FindProfileUnsafe(policyApp);
                _currentBrowserProfile = profile?.Name ?? "Không có profile";
                _currentBrowserAllowanceRemainingSeconds = profile is null ? 0 : GetAllowanceRemainingSecondsUnsafe(profile);
                _currentBrowserAccess = AccessModeShortLabel(EntertainmentAccess.Blocked);
                SetEntertainmentContextUnsafe(policyApp, EntertainmentAccess.Blocked);
            }
        }
        else if (rule?.Category == AppCategory.Entertainment)
        {
            var policyApp = BrowserRuleAsTrackedAppUnsafe(rule, sample);
            var profile = FindProfileUnsafe(policyApp);
            _currentBrowserProfile = profile?.Name ?? "Không có profile";
            _currentBrowserAllowanceRemainingSeconds = profile is null ? 0 : GetAllowanceRemainingSecondsUnsafe(profile);
            access = GetEntertainmentAccessUnsafe(policyApp, out browserBlockReason);
            _currentBrowserAccess = AccessModeShortLabel(access);
            _currentBrowserBlocked = access == EntertainmentAccess.Blocked;
            if (browserForegroundVerified)
            {
                SetEntertainmentContextUnsafe(policyApp, _currentBrowserBlocked ? EntertainmentAccess.Blocked : access);
                _currentMode = _currentBrowserBlocked
                    ? $"Giải trí web đang bị khóa · {browserBlockReason}"
                    : AccessModeLabel(access).Replace("Đang giải trí", "Đang giải trí trên web");
                _currentApp = policyApp.Name;
            }
        }
        else
        {
            if (browserForegroundVerified && sample.DocumentVisible) ClearEntertainmentContextUnsafe();
            _currentBrowserProfile = "—";
            _currentBrowserAccess = rule?.Category == AppCategory.Focus ? "Focus" : "—";
            _currentBrowserAllowanceRemainingSeconds = 0;
            _currentBrowserBlocked = false;
        }

        // V7.5.6: NativeHost supplies monotonic elapsed time ONLY while the actual
        // Win32 foreground window belongs to this browser. Therefore browser time
        // no longer depends on the desktop-agent heartbeat at all.
        if (!string.Equals(_lastBrowserAccountingRuleId, rule?.Id ?? "", StringComparison.Ordinal))
        {
            _lastBrowserAccountingRuleId = rule?.Id ?? "";
            _browserAccountingCarrySeconds = 0;
        }

        var verifiedElapsedSeconds =
            browserForegroundVerified
                ? Math.Clamp(sample.ActiveElapsedMilliseconds, 0, 2500) / 1000.0
                : 0.0;

        _browserAccountingCarrySeconds = Math.Min(
            3.0,
            _browserAccountingCarrySeconds + verifiedElapsedSeconds);

        var wholeSeconds = (int)Math.Floor(_browserAccountingCarrySeconds);
        if (wholeSeconds > 0)
            _browserAccountingCarrySeconds -= wholeSeconds;

        if (rule is not null && wholeSeconds > 0)
        {
            var trackedBrowser = BrowserRuleAsTrackedAppUnsafe(rule, sample);

            if (rule.Category == AppCategory.Focus)
            {
                if (browserFocusOk)
                {
                    for (var i = 0; i < wholeSeconds; i++)
                        CreditFocusSecond(trackedBrowser);

                    ClearEntertainmentContextUnsafe();
                    _currentMode = _browserMediaQualified
                        ? "Đang học trên web (media đang phát)"
                        : "Đang học trên web";
                    _currentApp = trackedBrowser.Name;
                }
            }
            else if (!IsWhitelistSessionActiveUnsafe())
            {
                for (var i = 0; i < wholeSeconds; i++)
                {
                    var liveAccess = GetEntertainmentAccessUnsafe(trackedBrowser, out var liveReason);
                    if (liveAccess == EntertainmentAccess.Blocked)
                    {
                        _currentBrowserBlocked = true;
                        browserBlockReason = liveReason;
                        break;
                    }

                    ConsumeEntertainmentSecondUnsafe(trackedBrowser, liveAccess);
                    _state.TotalEntertainmentSeconds++;
                    RecordUsageSecondUnsafe(trackedBrowser, AppCategory.Entertainment);
                }

                var finalAccess = GetEntertainmentAccessUnsafe(trackedBrowser, out var finalReason);
                access = finalAccess;
                _currentBrowserBlocked = finalAccess == EntertainmentAccess.Blocked;
                _currentBrowserAccess = AccessModeShortLabel(finalAccess);

                var liveProfile = FindProfileUnsafe(trackedBrowser);
                _currentBrowserProfile = liveProfile?.Name ?? "Không có profile";
                _currentBrowserAllowanceRemainingSeconds =
                    liveProfile is null ? 0 : GetAllowanceRemainingSecondsUnsafe(liveProfile);

                SetEntertainmentContextUnsafe(trackedBrowser, finalAccess);
                _currentMode = _currentBrowserBlocked
                    ? $"Giải trí web đang bị khóa · {finalReason}"
                    : AccessModeLabel(finalAccess).Replace("Đang giải trí", "Đang giải trí trên web");
                _currentApp = trackedBrowser.Name;
            }
        }

        var message = _currentBrowserBlocked && IsWhitelistSessionActiveUnsafe()
            ? "Focus-only đang bật: chỉ website Học/Làm việc được phép."
            : rule is null
                ? "Website chưa có rule FocusLock."
                : _currentBrowserBlocked
                    ? $"Đã khóa {rule.DisplayName}: {browserBlockReason}."
                    : rule.Category == AppCategory.Focus
                        ? browserFocusOk
                            ? _browserMediaQualified
                                ? $"{rule.DisplayName}: video/audio đang phát, Focus được tính."
                                : $"{rule.DisplayName}: tab đang hoạt động, Focus được tính."
                            : $"{rule.DisplayName}: chưa đủ hoạt động. Click, gõ hoặc cuộn để bắt đầu."
                        : access switch
                        {
                            EntertainmentAccess.Free => $"{rule.DisplayName}: profile đang cho phép miễn phí.",
                            EntertainmentAccess.Allowance => $"{rule.DisplayName}: đang dùng allowance của profile.",
                            EntertainmentAccess.Wallet => $"{rule.DisplayName}: đang dùng ví Focus.",
                            _ => $"Đã phân loại {rule.DisplayName} → Giải trí."
                        };

        return new BrowserDecision
        {
            BridgeOnline = true,
            Matched = rule is not null,
            Blocked = _currentBrowserBlocked,
            Category = _currentBrowserCategory,
            RuleId = rule?.Id ?? "",
            RuleName = rule?.DisplayName ?? "",
            Host = sample.Host,
            Url = sample.Url,
            Message = message,
            EntertainmentBalanceSeconds = _state.EntertainmentBalanceSeconds,
            FocusProgressSeconds = _state.FocusProgressSeconds,
            ProfileName = _currentBrowserProfile,
            AccessMode = _currentBrowserAccess,
            AllowanceRemainingSeconds = _currentBrowserAllowanceRemainingSeconds,
            AccountedSeconds = wholeSeconds
        };
    }

    private TrackedApp BrowserRuleAsTrackedAppUnsafe(BrowserRule rule, BrowserContextSample sample) => new()
    {
        Id = "browser:" + rule.Id,
        Name = $"{BrowserDisplayName(sample.Browser)} · {rule.DisplayName}",
        ProcessName = sample.Browser,
        Category = rule.Category,
        Enabled = true,
        BlockProfileId = rule.BlockProfileId,
        BlockProfileName = rule.BlockProfileName
    };

    // Kept as a compatibility stub for older diagnostics. V7.5.3 charges browser
    // entertainment from ApplyActivity(), where foreground ownership is authoritative.
    private void AccountBrowserEntertainmentGuardTickUnsafe()
    {
        _browserEntertainmentGuardCarrySeconds = 0;
        _lastBrowserEntertainmentGuardRuleId = "";
        _lastBrowserEntertainmentGuardTick = 0;
    }

    private bool BrowserFocusQualifiedUnsafe(DateTime now)
    {
        if (!_state.Settings.AntiCheatEnabled) return true;
        if (_browserMediaQualified) return true;
        if (_browserEngagedUntilUtc > now) return true;
        return _browserActivityEvents.Count >= Math.Max(1, _state.Settings.MinimumActivityEventsPerMinute);
    }

    private TrackedApp? ResolveBrowserTrackedUnsafe(string processName, string path)
    {
        if (!_state.Settings.BrowserRulesEnabled || !BrowserBridgeHealthyUnsafe() || _browserContext is null ||
            !BrowserForegroundActiveUnsafe(_browserContext.Browser))
            return null;
        if (!BrowserMatchesProcess(_browserContext.Browser, processName)) return null;

        var rule = FindBrowserRuleUnsafe(_browserContext.Url, _browserContext.Title);
        if (rule is null) return null;

        return new TrackedApp
        {
            Id = "browser:" + rule.Id,
            Name = $"{BrowserDisplayName(_browserContext.Browser)} · {rule.DisplayName}",
            ExePath = path,
            ProcessName = processName,
            Category = rule.Category,
            Enabled = true,
            BlockProfileId = rule.BlockProfileId,
            BlockProfileName = rule.BlockProfileName
        };
    }

    private BrowserRule? FindBrowserRuleUnsafe(string url, string title)
    {
        var host = ExtractHost(url);
        return _state.BrowserRules
            .Where(r => IsBrowserRulePolicyEnabledUnsafe(r) && !string.IsNullOrWhiteSpace(r.Pattern) && BrowserRuleMatches(r, url, title, host))
            .OrderByDescending(BrowserRuleSpecificity)
            .ThenByDescending(r => r.Pattern.Length)
            .FirstOrDefault();
    }

    private static bool BrowserRuleMatches(BrowserRule rule, string url, string title, string host)
    {
        var pattern = rule.Pattern.Trim();
        if (pattern.Length == 0) return false;
        return rule.MatchType switch
        {
            BrowserRuleMatchType.HostSuffix => HostMatches(host, pattern),
            BrowserRuleMatchType.UrlPrefix => url.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            BrowserRuleMatchType.UrlContains => url.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            BrowserRuleMatchType.TitleContains => title.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static int BrowserRuleSpecificity(BrowserRule rule) => (rule.MatchType switch
    {
        BrowserRuleMatchType.UrlPrefix => 4000,
        BrowserRuleMatchType.TitleContains => 3000,
        BrowserRuleMatchType.UrlContains => 2500,
        BrowserRuleMatchType.HostSuffix => 1000,
        _ => 0
    }) + Math.Min(999, rule.Pattern.Length);

    private static bool HostMatches(string host, string pattern)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        pattern = pattern.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        return host == pattern || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.Host.ToLowerInvariant()
            : "";
    }

    private bool BrowserForegroundActiveUnsafe(string browser)
    {
        // Preferred source: NativeHost, which runs in the interactive user session
        // and verifies GetForegroundWindow() directly.
        if (_browserContext is not null &&
            _browserContext.WindowFocused &&
            BrowserBridgeHealthyUnsafe() &&
            NormalizeBrowserName(_browserContext.Browser) == NormalizeBrowserName(browser))
        {
            return true;
        }

        // Fallback for desktop-only scenarios.
        if (_lastVerifiedForegroundTick == 0) return false;
        var age = (Stopwatch.GetTimestamp() - _lastVerifiedForegroundTick) / (double)Stopwatch.Frequency;
        if (age < 0 || age > 3.0) return false;
        return BrowserMatchesProcess(NormalizeBrowserName(browser), _lastVerifiedForegroundProcess);
    }

    private static bool IsSupportedBrowserProcess(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedge", StringComparison.OrdinalIgnoreCase);

    private static bool BrowserMatchesProcess(string browser, string processName) =>
        (browser == "chrome" && processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) ||
        (browser == "edge" && processName.Equals("msedge", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeBrowserName(string browser)
    {
        browser = (browser ?? "").Trim().ToLowerInvariant();
        return browser.Contains("edge") || browser.Contains("edg") ? "edge" : "chrome";
    }

    private static string BrowserDisplayName(string browser) => NormalizeBrowserName(browser) == "edge" ? "Edge" : "Chrome";
    private static string TrimTo(string? value, int max) => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    private TrackedApp? FindTracked(string processName, string path)
    {
        foreach (var app in _state.Apps.Where(IsAppPolicyEnabledUnsafe))
        {
            if (!string.Equals(app.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(app.ExePath) && !string.IsNullOrWhiteSpace(path) && !PathsEqual(app.ExePath, path)) continue;

            // Với app Focus, hash giúp ngăn thay file/giả mạo executable để kiếm thời gian.
            // Với Entertainment, direct path/name vẫn phải được coi là giải trí để balance luôn bị trừ sau khi app update.
            if (app.Category == AppCategory.Focus && _state.Settings.VerifyExecutableHash &&
                !string.IsNullOrWhiteSpace(app.Sha256) && !string.IsNullOrWhiteSpace(path))
            {
                var actualHash = GetCachedHash(path);
                if (!string.Equals(app.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    _currentMode = "Ứng dụng Focus đã thay đổi file – cần thêm lại";
                    return null;
                }
            }
            return app;
        }

        // Bắt trường hợp đổi tên executable: đối chiếu hash đã đăng ký.
        if (_state.Settings.VerifyExecutableHash && !string.IsNullOrWhiteSpace(path))
        {
            var actualHash = GetCachedHash(path);
            if (!string.IsNullOrWhiteSpace(actualHash))
            {
                var byHash = _state.Apps.FirstOrDefault(a => IsAppPolicyEnabledUnsafe(a) && !string.IsNullOrWhiteSpace(a.Sha256) &&
                    string.Equals(a.Sha256, actualHash, StringComparison.OrdinalIgnoreCase));
                if (byHash is not null) return byHash;
            }
        }
        return null;
    }

    private string AddApp(TrackedApp? app)
    {
        if (app is null) throw new InvalidOperationException("Thiếu ứng dụng.");
        if (string.IsNullOrWhiteSpace(app.ProcessName)) throw new InvalidOperationException("ProcessName không hợp lệ.");
        if (ProtectedProcessNames.Contains(app.ProcessName))
            throw new InvalidOperationException("Không thể thêm process hệ thống/FocusLock này vì có thể làm Windows mất ổn định.");
        if (_state.Apps.Any(a => !string.IsNullOrWhiteSpace(app.ExePath) && PathsEqual(a.ExePath, app.ExePath)))
            throw new InvalidOperationException("Ứng dụng này đã có trong danh sách.");
        if (!string.IsNullOrWhiteSpace(app.Sha256) && _state.Apps.Any(a => a.Category != app.Category && string.Equals(a.Sha256, app.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cùng một executable/hash không thể vừa là Focus vừa là Entertainment.");
        app.Id = string.IsNullOrWhiteSpace(app.Id) ? Guid.NewGuid().ToString("N") : app.Id;
        if (app.Category == AppCategory.Entertainment)
        {
            var profile = GetDefaultBlockProfileUnsafe();
            app.BlockProfileId = profile.Id;
            app.BlockProfileName = profile.Name;
        }
        _state.Apps.Add(app);
        AddAudit("Config", $"Thêm {app.Name} vào nhóm {app.CategoryLabel}.");
        _store.Save(_state);
        return "Đã thêm ứng dụng.";
    }

    private string RemoveApp(string? id)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        _state.Apps.Remove(app);
        AddAudit("Config", $"Xóa {app.Name} khỏi danh sách.");
        _store.Save(_state);
        return "Đã xóa ứng dụng.";
    }

    private string ToggleApp(string? id)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        app.Enabled = !app.Enabled;
        AddAudit("Config", $"{(app.Enabled ? "Bật" : "Tắt")} theo dõi {app.Name}.");
        _store.Save(_state);
        return app.Enabled ? "Đã bật ứng dụng." : "Đã tắt ứng dụng.";
    }

    private string CycleAppBlockAction(string? id)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        if (app.Category != AppCategory.Entertainment) throw new InvalidOperationException("Chế độ khóa chỉ áp dụng cho ứng dụng giải trí.");
        app.BlockAction = app.BlockAction switch
        {
            EntertainmentBlockAction.Close => EntertainmentBlockAction.Suspend,
            EntertainmentBlockAction.Suspend => EntertainmentBlockAction.BlockLaunch,
            _ => EntertainmentBlockAction.Close
        };
        AddAudit("Block", $"{app.Name}: chế độ khóa → {app.BlockActionLabel}.");
        _store.Save(_state);
        return $"{app.Name}: {app.BlockActionLabel}.";
    }

    private string CycleAppProfile(string? id)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        if (app.Category != AppCategory.Entertainment) throw new InvalidOperationException("Profile khóa chỉ áp dụng cho ứng dụng giải trí.");
        var profiles = _state.BlockProfiles.OrderBy(p => p.CreatedUtc).ToList();
        if (profiles.Count == 0) profiles.Add(GetDefaultBlockProfileUnsafe());
        var index = profiles.FindIndex(p => p.Id == app.BlockProfileId);
        var next = profiles[(index + 1 + profiles.Count) % profiles.Count];
        app.BlockProfileId = next.Id;
        app.BlockProfileName = next.Name;
        AddAudit("Block", $"{app.Name}: chuyển sang profile {next.Name}.");
        _store.Save(_state);
        return $"{app.Name}: profile {next.Name}.";
    }

    private string SetAppProfile(string? appId, string? profileId)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == appId) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        if (app.Category != AppCategory.Entertainment) throw new InvalidOperationException("Profile chỉ áp dụng cho ứng dụng giải trí.");
        var profile = _state.BlockProfiles.FirstOrDefault(p => p.Id == profileId) ?? throw new InvalidOperationException("Không tìm thấy profile.");
        app.BlockProfileId = profile.Id;
        app.BlockProfileName = profile.Name;
        AddAudit("Block", $"{app.Name}: gán vào profile {profile.Name}.");
        _store.Save(_state);
        return $"Đã gán {app.Name} vào {profile.Name}.";
    }

    private string SetAppBlockAction(string? appId, bool useCustom, EntertainmentBlockAction action)
    {
        var app = _state.Apps.FirstOrDefault(a => a.Id == appId) ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
        if (app.Category != AppCategory.Entertainment) throw new InvalidOperationException("Cách khóa chỉ áp dụng cho ứng dụng giải trí.");
        app.UseCustomBlockAction = useCustom;
        app.BlockAction = action;
        AddAudit("Block", useCustom
            ? $"{app.Name}: dùng cách khóa riêng {app.BlockActionLabel}."
            : $"{app.Name}: dùng cách khóa mặc định của profile.");
        _store.Save(_state);
        return useCustom ? $"{app.Name}: {app.BlockActionLabel}." : $"{app.Name}: theo Profile.";
    }

    private string AddBlockProfile(BlockProfile? profile)
    {
        if (profile is null) throw new InvalidOperationException("Thiếu block profile.");
        var name = TrimTo((profile.Name ?? "").Trim(), 80);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Tên profile không được để trống.");
        if (_state.BlockProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Profile này đã tồn tại.");
        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = name;
        profile.Enabled = true;
        profile.CreatedUtc = DateTime.UtcNow;
        if (profile.PolicyVersion < 2)
        {
            MigrateLegacyProfilePolicyUnsafe(profile);
            profile.PolicyVersion = 2;
        }
        profile.WeeklyScheduleMask = BlockProfile.IsValidMask(profile.WeeklyScheduleMask)
            ? profile.WeeklyScheduleMask
            : new string('0', 336);
        _state.BlockProfiles.Add(profile);
        AddAudit("Block", $"Tạo profile {name}.");
        _store.Save(_state);
        return $"Đã tạo profile {name}.";
    }

    private static void ApplyProfilePresetUnsafe(BlockProfile profile)
    {
        var name = (profile.Name ?? "").ToLowerInvariant();
        if (name.Contains("game") || name.Contains("trò chơi"))
        {
            profile.Mode = BlockProfileMode.EarnedTime;
            profile.DailyAllowanceMinutes = 0;
            profile.OverrideAppBlockAction = true;
            profile.DefaultBlockAction = EntertainmentBlockAction.Close;
            return;
        }

        if (name.Contains("mạng") || name.Contains("social") || name.Contains("facebook") || name.Contains("chat"))
        {
            profile.Mode = BlockProfileMode.AllowanceThenEarned;
            if (profile.DailyAllowanceMinutes <= 0) profile.DailyAllowanceMinutes = 20;
            profile.OverrideAppBlockAction = true;
            profile.DefaultBlockAction = EntertainmentBlockAction.Suspend;
            return;
        }

        if (name.Contains("video") || name.Contains("phim") || name.Contains("youtube"))
        {
            profile.Mode = BlockProfileMode.AllowanceThenEarned;
            if (profile.DailyAllowanceMinutes <= 0) profile.DailyAllowanceMinutes = 30;
            profile.OverrideAppBlockAction = true;
            profile.DefaultBlockAction = EntertainmentBlockAction.Suspend;
        }
    }

    private string ToggleBlockProfile(string? id)
    {
        var profile = _state.BlockProfiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Không tìm thấy profile.");
        profile.Enabled = !profile.Enabled;
        AddAudit("Block", $"{(profile.Enabled ? "Bật" : "Tạm tắt")} profile {profile.Name}.");
        _store.Save(_state);
        return profile.Enabled ? "Đã bật profile." : "Đã tạm tắt profile.";
    }

    private string RemoveBlockProfile(string? id)
    {
        var profile = _state.BlockProfiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Không tìm thấy profile.");
        if (_state.BlockProfiles.Count <= 1) throw new InvalidOperationException("Phải giữ lại ít nhất một profile.");
        var fallback = _state.BlockProfiles.FirstOrDefault(p => p.Id != profile.Id && string.Equals(p.Name, "Giải trí chung", StringComparison.OrdinalIgnoreCase))
                       ?? _state.BlockProfiles.First(p => p.Id != profile.Id);
        foreach (var app in _state.Apps.Where(a => a.BlockProfileId == profile.Id))
        {
            app.BlockProfileId = fallback.Id;
            app.BlockProfileName = fallback.Name;
        }
        foreach (var rule in _state.BrowserRules.Where(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == profile.Id))
        {
            rule.BlockProfileId = fallback.Id;
            rule.BlockProfileName = fallback.Name;
        }
        _state.BlockProfiles.Remove(profile);
        AddAudit("Block", $"Xóa profile {profile.Name}; ứng dụng được chuyển sang {fallback.Name}.");
        _store.Save(_state);
        return "Đã xóa profile.";
    }

    private BlockProfile GetDefaultBlockProfileUnsafe()
    {
        var profile = _state.BlockProfiles.FirstOrDefault(p => string.Equals(p.Name, "Giải trí chung", StringComparison.OrdinalIgnoreCase))
                      ?? _state.BlockProfiles.FirstOrDefault();
        if (profile is not null) return profile;
        profile = new BlockProfile { Name = "Giải trí chung", Enabled = true };
        _state.BlockProfiles.Add(profile);
        return profile;
    }

    private static bool IsAppPolicyEnabledUnsafe(TrackedApp app) => app.Enabled;

    private static readonly HashSet<string> ConfigurationMutationCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "addapp", "removeapp", "toggleapp", "cycleapplock", "cycleappprofile", "setappprofile", "setappblockaction",
        "addblockprofile", "toggleblockprofile", "removeblockprofile", "updateblockprofile",
        "settings", "addbrowserrule", "removebrowserrule", "togglebrowserrule", "cyclebrowserprofile", "setbrowserprofile"
    };

    private static bool IsConfigurationMutationCommand(string command) =>
        ConfigurationMutationCommands.Contains(command);

    private void EnsureConfigurationChangeAllowedUnsafe()
    {
        var policy = _state.ControlPolicy;
        if (policy.SettingsTextProtectionActive)
            throw new InvalidOperationException("Bảo vệ cài đặt đang bật. Muốn sửa cấu hình phải gõ đúng đoạn xác nhận trong mục Kiểm soát.");
        if (policy.SettingsTimeProtectionActive)
        {
            var until = policy.SettingsProtectionUntilUtc!.Value.ToLocalTime();
            throw new InvalidOperationException($"Bảo vệ cài đặt theo thời gian đang bật. Không thể sửa cấu hình tới {until:dd/MM/yyyy HH:mm:ss}.");
        }
        if (IsLockedSessionActiveUnsafe())
            throw new InvalidOperationException("Locked Session đang chạy. Không thể thay đổi cấu hình cho tới khi phiên kết thúc.");
        if (IsWhitelistSessionActiveUnsafe())
            throw new InvalidOperationException("Focus-only Whitelist đang chạy. Không thể thay đổi cấu hình cho tới khi phiên kết thúc.");
        if (_state.ControlPolicy.StrictModeEnabled)
        {
            var available = _state.ControlPolicy.StrictUnlockAvailableUtc;
            var hint = available is DateTime at
                ? $" Yêu cầu mở đã được tạo; có thể tắt Strict sau {at.ToLocalTime():HH:mm:ss}."
                : " Hãy bấm 'Yêu cầu mở khóa' trong mục Kiểm soát.";
            throw new InvalidOperationException("Strict Mode đang khóa thay đổi cấu hình." + hint);
        }
    }

    private string EnableSettingsTextProtection()
    {
        if (_state.ControlPolicy.SettingsTextProtectionActive ||
            (_state.ControlPolicy.SettingsProtectionMode == SettingsProtectionMode.TimeWindow &&
             _state.ControlPolicy.SettingsProtectionUntilUtc is DateTime existingUntil && existingUntil > DateTime.UtcNow))
            throw new InvalidOperationException("Bảo vệ cài đặt đã được bật hoặc lên lịch; không thể ghi đè cam kết hiện tại.");

        var token = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        _state.ControlPolicy.SettingsProtectionMode = SettingsProtectionMode.TypingChallenge;
        _state.ControlPolicy.SettingsProtectionStartUtc = DateTime.UtcNow;
        _state.ControlPolicy.SettingsProtectionUntilUtc = null;
        _state.ControlPolicy.SettingsUnlockChallenge =
            $"Tôi xác nhận rằng FocusLock đang bảo vệ thời gian và kỷ luật mà tôi đã tự đặt ra. Tôi chỉ thay đổi cấu hình khi thật sự cần thiết, không phải vì muốn né một giới hạn trong lúc mất kiên nhẫn. Tôi hiểu rằng việc gõ hết đoạn này là một khoảng dừng có chủ ý trước khi sửa quy tắc. Mã xác nhận {token}.";
        AddAudit("Protection", "Bật bảo vệ cài đặt bằng đoạn văn xác nhận.");
        _store.Save(_state);
        return "Đã bật bảo vệ cài đặt bằng đoạn văn. Muốn sửa cấu hình phải gõ chính xác đoạn xác nhận.";
    }

    private string UnlockSettingsTextProtection(string? typed)
    {
        var policy = _state.ControlPolicy;
        if (!policy.SettingsTextProtectionActive)
            throw new InvalidOperationException("Bảo vệ bằng đoạn văn hiện không hoạt động.");

        static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
        if (!string.Equals(Normalize(typed ?? ""), Normalize(policy.SettingsUnlockChallenge), StringComparison.Ordinal))
            throw new InvalidOperationException("Đoạn xác nhận chưa chính xác. Hãy gõ đầy đủ, đúng từng ký tự.");

        policy.SettingsProtectionMode = SettingsProtectionMode.Off;
        policy.SettingsUnlockChallenge = "";
        policy.SettingsProtectionStartUtc = null;
        policy.SettingsProtectionUntilUtc = null;
        AddAudit("Protection", "Đã gỡ bảo vệ cài đặt bằng đoạn văn xác nhận.");
        _store.Save(_state);
        return "Đã mở khóa thay đổi cấu hình.";
    }

    private string EnableSettingsTimeProtection(DateTime? startUtc, DateTime? untilUtc)
    {
        if (_state.ControlPolicy.SettingsTextProtectionActive ||
            (_state.ControlPolicy.SettingsProtectionMode == SettingsProtectionMode.TimeWindow &&
             _state.ControlPolicy.SettingsProtectionUntilUtc is DateTime existingUntil && existingUntil > DateTime.UtcNow))
            throw new InvalidOperationException("Bảo vệ cài đặt đã được bật hoặc lên lịch; không thể ghi đè cam kết hiện tại.");
        if (startUtc is null || untilUtc is null)
            throw new InvalidOperationException("Thiếu thời điểm bắt đầu/kết thúc.");

        var now = DateTime.UtcNow;
        var start = startUtc.Value.Kind == DateTimeKind.Utc ? startUtc.Value : startUtc.Value.ToUniversalTime();
        var until = untilUtc.Value.Kind == DateTimeKind.Utc ? untilUtc.Value : untilUtc.Value.ToUniversalTime();
        if (start < now.AddMinutes(-1)) start = now;
        if (until <= start.AddMinutes(1))
            throw new InvalidOperationException("Thời điểm kết thúc phải sau thời điểm bắt đầu ít nhất 2 phút.");
        if (until > now.AddDays(365))
            throw new InvalidOperationException("Bảo vệ cài đặt theo thời gian tối đa 365 ngày.");

        var policy = _state.ControlPolicy;
        policy.SettingsProtectionMode = SettingsProtectionMode.TimeWindow;
        policy.SettingsUnlockChallenge = "";
        policy.SettingsProtectionStartUtc = start;
        policy.SettingsProtectionUntilUtc = until;
        AddAudit("Protection", $"Đặt bảo vệ cài đặt từ {start.ToLocalTime():dd/MM HH:mm} tới {until.ToLocalTime():dd/MM HH:mm}.");
        _store.Save(_state);
        return start <= now
            ? $"Đã khóa thay đổi cấu hình tới {until.ToLocalTime():dd/MM/yyyy HH:mm:ss}. Không thể mở sớm."
            : $"Đã lên lịch bảo vệ cấu hình từ {start.ToLocalTime():dd/MM/yyyy HH:mm} tới {until.ToLocalTime():dd/MM/yyyy HH:mm}.";
    }

    private string EnableStrictMode(int delayMinutes)
    {
        if (_state.ControlPolicy.StrictModeEnabled)
            throw new InvalidOperationException("Strict Mode đã bật; không thể thay đổi thời gian chờ cho tới khi tắt Strict đúng quy trình.");
        delayMinutes = Math.Clamp(delayMinutes <= 0 ? 30 : delayMinutes, 1, 1440);
        _state.ControlPolicy.StrictModeEnabled = true;
        _state.ControlPolicy.StrictUnlockDelayMinutes = delayMinutes;
        _state.ControlPolicy.StrictUnlockRequestedUtc = null;
        AddAudit("Strict", $"Bật Strict Mode; thời gian chờ mở khóa {delayMinutes} phút.");
        _store.Save(_state);
        return $"Strict Mode đã bật. Muốn tắt phải yêu cầu mở khóa và chờ {delayMinutes} phút.";
    }

    private string RequestStrictUnlock()
    {
        if (!_state.ControlPolicy.StrictModeEnabled) return "Strict Mode đang tắt.";
        if (_state.ControlPolicy.StrictUnlockRequestedUtc is null)
        {
            _state.ControlPolicy.StrictUnlockRequestedUtc = DateTime.UtcNow;
            AddAudit("Strict", "Đã yêu cầu mở khóa Strict Mode.");
            _store.Save(_state);
        }
        var ready = _state.ControlPolicy.StrictUnlockAvailableUtc!.Value.ToLocalTime();
        return $"Có thể tắt Strict Mode sau {ready:dd/MM HH:mm:ss}.";
    }

    private string DisableStrictMode()
    {
        if (!_state.ControlPolicy.StrictModeEnabled) return "Strict Mode đã tắt.";
        if (!_state.ControlPolicy.StrictUnlockReady)
        {
            if (_state.ControlPolicy.StrictUnlockRequestedUtc is null)
                throw new InvalidOperationException("Chưa yêu cầu mở khóa Strict Mode.");
            var ready = _state.ControlPolicy.StrictUnlockAvailableUtc!.Value.ToLocalTime();
            throw new InvalidOperationException($"Chưa hết thời gian chờ. Có thể tắt Strict Mode sau {ready:dd/MM HH:mm:ss}.");
        }
        _state.ControlPolicy.StrictModeEnabled = false;
        _state.ControlPolicy.StrictUnlockRequestedUtc = null;
        AddAudit("Strict", "Đã tắt Strict Mode sau khi hết thời gian chờ.");
        _store.Save(_state);
        return "Strict Mode đã tắt.";
    }

    private string StartLockedSession(int durationMinutes)
    {
        durationMinutes = ValidateSessionMinutes(durationMinutes);
        var proposed = DateTime.UtcNow.AddMinutes(durationMinutes);
        var current = _state.ControlPolicy.LockedSessionUntilUtc;
        _state.ControlPolicy.LockedSessionUntilUtc = current is DateTime until && until > proposed ? until : proposed;
        AddAudit("Strict", $"Bắt đầu Locked Session {durationMinutes} phút; chặn toàn bộ giải trí đã khai báo.");
        _store.Save(_state);
        return $"Locked Session đang chạy tới {_state.ControlPolicy.LockedSessionUntilUtc!.Value.ToLocalTime():HH:mm:ss}.";
    }

    private string StartWhitelistSession(int durationMinutes)
    {
        durationMinutes = ValidateSessionMinutes(durationMinutes);
        var proposed = DateTime.UtcNow.AddMinutes(durationMinutes);
        var current = _state.ControlPolicy.WhitelistSessionUntilUtc;
        _state.ControlPolicy.WhitelistSessionUntilUtc = current is DateTime until && until > proposed ? until : proposed;
        AddAudit("Strict", $"Bắt đầu Focus-only Whitelist {durationMinutes} phút; browser chỉ cho rule Focus và app giải trí đã khai báo bị khóa.");
        _store.Save(_state);
        return $"Focus-only Whitelist đang chạy tới {_state.ControlPolicy.WhitelistSessionUntilUtc!.Value.ToLocalTime():HH:mm:ss}.";
    }

    private static int ValidateSessionMinutes(int minutes)
    {
        if (minutes < 1 || minutes > 1440)
            throw new InvalidOperationException("Thời lượng phiên phải từ 1 đến 1440 phút.");
        return minutes;
    }

    private bool IsLockedSessionActiveUnsafe() =>
        _state.ControlPolicy.LockedSessionUntilUtc is DateTime until && until > DateTime.UtcNow;

    private bool IsWhitelistSessionActiveUnsafe() =>
        _state.ControlPolicy.WhitelistSessionUntilUtc is DateTime until && until > DateTime.UtcNow;

    private string UpdateBlockProfile(BlockProfile? requested)
    {
        if (requested is null || string.IsNullOrWhiteSpace(requested.Id))
            throw new InvalidOperationException("Thiếu block profile.");
        var profile = _state.BlockProfiles.FirstOrDefault(p => p.Id == requested.Id)
                      ?? throw new InvalidOperationException("Không tìm thấy profile.");

        if (requested.DailyAllowanceMinutes < 0 || requested.DailyAllowanceMinutes > 1440)
            throw new InvalidOperationException("Allowance phải từ 0 đến 1440 phút/ngày.");
        if (!Enum.IsDefined(requested.DefaultAccessPolicy) || !Enum.IsDefined(requested.ScheduledAccessPolicy))
            throw new InvalidOperationException("Chính sách truy cập của Block Profile không hợp lệ.");
        if (!Enum.IsDefined(requested.DefaultBlockAction))
            throw new InvalidOperationException("Cách khóa ứng dụng không hợp lệ.");
        if (requested.ScheduleEnabled && !BlockProfile.IsValidMask(requested.WeeklyScheduleMask))
            throw new InvalidOperationException("Lịch tuần không hợp lệ. Hãy mở Lịch tuần và lưu lại.");

        var requestedName = TrimTo((requested.Name ?? "").Trim(), 80);
        if (string.IsNullOrWhiteSpace(requestedName)) throw new InvalidOperationException("Tên profile không được để trống.");
        if (_state.BlockProfiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Tên profile này đã tồn tại.");
        profile.Name = requestedName;
        profile.Enabled = requested.Enabled;
        profile.PolicyVersion = 2;
        profile.DefaultAccessPolicy = requested.DefaultAccessPolicy;
        profile.ScheduledAccessPolicy = requested.ScheduledAccessPolicy;
        profile.Mode = requested.Mode; // legacy field, no longer drives access.
        profile.OverrideAppBlockAction = true; // legacy compatibility.
        profile.DefaultBlockAction = requested.DefaultBlockAction;
        profile.ScheduleEnabled = requested.ScheduleEnabled;
        profile.WeeklyScheduleMask = BlockProfile.IsValidMask(requested.WeeklyScheduleMask)
            ? requested.WeeklyScheduleMask
            : new string('0', 336);
        profile.DailyAllowanceMinutes = requested.DailyAllowanceMinutes;

        ResetAllowanceIfNewDayUnsafe(profile, DateTime.Now);
        profile.AllowanceUsedSeconds = Math.Min(profile.AllowanceUsedSeconds, profile.DailyAllowanceMinutes * 60);

        foreach (var app in _state.Apps.Where(a => a.BlockProfileId == profile.Id)) app.BlockProfileName = profile.Name;
        foreach (var rule in _state.BrowserRules.Where(r => r.BlockProfileId == profile.Id)) rule.BlockProfileName = profile.Name;
        AddAudit("Block", $"Cập nhật {profile.Name}: ngoài lịch {profile.DefaultAccessLabel}; trong lịch {profile.ScheduledAccessLabel}; {profile.ScheduleLabel}; allowance {profile.DailyAllowanceMinutes} phút/ngày.");
        _store.Save(_state);
        return $"Đã lưu chính sách cho {profile.Name}.";
    }

    private enum EntertainmentAccess
    {
        Free,
        Allowance,
        Wallet,
        Blocked
    }

    private bool ShouldLockEntertainmentAppUnsafe(TrackedApp app, out string reason) =>
        GetEntertainmentAccessUnsafe(app, out reason) == EntertainmentAccess.Blocked;

    private EntertainmentAccess GetEntertainmentAccessUnsafe(TrackedApp app, out string reason)
    {
        reason = "";

        if (!app.Enabled)
        {
            reason = "rule đang tắt";
            return EntertainmentAccess.Free;
        }

        if (_state.ClockRollbackDetected)
        {
            reason = "thay đổi giờ hệ thống";
            return EntertainmentAccess.Blocked;
        }
        if (!HeartbeatHealthyUnsafe())
        {
            reason = "mất heartbeat";
            return EntertainmentAccess.Blocked;
        }
        if (IsLockedSessionActiveUnsafe())
        {
            reason = "Locked Session";
            return EntertainmentAccess.Blocked;
        }
        if (IsWhitelistSessionActiveUnsafe())
        {
            reason = "Focus-only Whitelist";
            return EntertainmentAccess.Blocked;
        }

        var profile = FindProfileUnsafe(app);
        if (profile is null)
        {
            if (_state.EntertainmentBalanceSeconds > 0) return EntertainmentAccess.Wallet;
            reason = "hết ví Focus";
            return EntertainmentAccess.Blocked;
        }

        if (!profile.Enabled)
        {
            reason = $"profile {profile.Name} đang tạm tắt";
            return EntertainmentAccess.Free;
        }

        var scheduled = profile.ScheduleEnabled && IsScheduleActiveUnsafe(profile, DateTime.Now);
        var policy = scheduled ? profile.ScheduledAccessPolicy : profile.DefaultAccessPolicy;
        return EvaluateProfileAccessPolicyUnsafe(profile, policy, scheduled, out reason);
    }

    private EntertainmentAccess EvaluateProfileAccessPolicyUnsafe(BlockProfile profile, ProfileAccessPolicy policy, bool scheduled, out string reason)
    {
        reason = "";
        switch (policy)
        {
            case ProfileAccessPolicy.Free:
                return EntertainmentAccess.Free;

            case ProfileAccessPolicy.Block:
                reason = scheduled ? $"lịch {profile.Name} đang khóa" : $"profile {profile.Name} khóa tuyệt đối";
                return EntertainmentAccess.Blocked;

            case ProfileAccessPolicy.AllowanceThenEarned:
                if (GetAllowanceRemainingSecondsUnsafe(profile) > 0) return EntertainmentAccess.Allowance;
                if (_state.EntertainmentBalanceSeconds > 0) return EntertainmentAccess.Wallet;
                reason = "hết allowance và ví Focus";
                return EntertainmentAccess.Blocked;

            case ProfileAccessPolicy.EarnedTime:
            default:
                if (_state.EntertainmentBalanceSeconds > 0) return EntertainmentAccess.Wallet;
                reason = "hết ví Focus";
                return EntertainmentAccess.Blocked;
        }
    }

    private EntertainmentBlockAction GetEffectiveBlockActionUnsafe(TrackedApp app)
    {
        if (app.UseCustomBlockAction) return app.BlockAction;
        var profile = FindProfileUnsafe(app);
        return profile is { Enabled: true } ? profile.DefaultBlockAction : app.BlockAction;
    }

    private static string AccessModeLabel(EntertainmentAccess access) => access switch
    {
        EntertainmentAccess.Free => "Đang giải trí · dùng tự do",
        EntertainmentAccess.Allowance => "Đang giải trí · dùng allowance",
        EntertainmentAccess.Wallet => "Đang giải trí · dùng ví Focus",
        _ => "Giải trí đang bị khóa"
    };

    private static string AccessModeShortLabel(EntertainmentAccess access) => access switch
    {
        EntertainmentAccess.Free => "Dùng tự do",
        EntertainmentAccess.Allowance => "Đang trừ allowance",
        EntertainmentAccess.Wallet => "Đang trừ ví Focus",
        _ => "Đang khóa"
    };

    private void ConsumeEntertainmentSecondUnsafe(TrackedApp app, EntertainmentAccess access)
    {
        switch (access)
        {
            case EntertainmentAccess.Allowance:
                var profile = FindProfileUnsafe(app);
                if (profile is not null && GetAllowanceRemainingSecondsUnsafe(profile) > 0)
                    profile.AllowanceUsedSeconds++;
                break;
            case EntertainmentAccess.Wallet:
                _state.EntertainmentBalanceSeconds = Math.Max(0, _state.EntertainmentBalanceSeconds - 1);
                break;
        }
    }

    private void SetEntertainmentContextUnsafe(TrackedApp app, EntertainmentAccess access)
    {
        _entertainmentSessionActive = true;
        _currentEntertainmentAccess = access;
        var profile = FindProfileUnsafe(app);
        _currentEntertainmentProfile = profile?.Name ?? "Không có profile";
        _currentEntertainmentAllowanceRemainingSeconds = profile is null ? 0 : GetAllowanceRemainingSecondsUnsafe(profile);
    }

    private void ClearEntertainmentContextUnsafe()
    {
        _entertainmentSessionActive = false;
        _currentEntertainmentAccess = EntertainmentAccess.Blocked;
        _currentEntertainmentProfile = "—";
        _currentEntertainmentAllowanceRemainingSeconds = 0;
    }

    private int CurrentEntertainmentUsableRemainingSecondsUnsafe()
    {
        if (!_entertainmentSessionActive) return 0;
        return _currentEntertainmentAccess switch
        {
            EntertainmentAccess.Free => int.MaxValue,
            EntertainmentAccess.Allowance => Math.Max(0, _currentEntertainmentAllowanceRemainingSeconds) + Math.Max(0, _state.EntertainmentBalanceSeconds),
            EntertainmentAccess.Wallet => Math.Max(0, _state.EntertainmentBalanceSeconds),
            _ => 0
        };
    }

    private BlockProfile? FindProfileUnsafe(TrackedApp app) =>
        string.IsNullOrWhiteSpace(app.BlockProfileId)
            ? null
            : _state.BlockProfiles.FirstOrDefault(p => p.Id == app.BlockProfileId);

    private int GetAllowanceRemainingSecondsUnsafe(BlockProfile profile)
    {
        ResetAllowanceIfNewDayUnsafe(profile, DateTime.Now);
        if (profile.DailyAllowanceMinutes <= 0) return 0;
        return Math.Max(0, profile.DailyAllowanceMinutes * 60 - profile.AllowanceUsedSeconds);
    }

    private void ResetDailyAllowancesUnsafe(DateTime localNow)
    {
        foreach (var profile in _state.BlockProfiles) ResetAllowanceIfNewDayUnsafe(profile, localNow);
    }

    private static void ResetAllowanceIfNewDayUnsafe(BlockProfile profile, DateTime localNow)
    {
        var key = localNow.ToString("yyyyMMdd");
        if (string.Equals(profile.AllowanceDateKey, key, StringComparison.Ordinal)) return;
        profile.AllowanceDateKey = key;
        profile.AllowanceUsedSeconds = 0;
    }

    private static void MigrateLegacyProfilePolicyUnsafe(BlockProfile profile)
    {
        switch (profile.Mode)
        {
            case BlockProfileMode.ScheduleBlock:
                profile.DefaultAccessPolicy = ProfileAccessPolicy.Free;
                profile.ScheduledAccessPolicy = ProfileAccessPolicy.Block;
                break;
            case BlockProfileMode.ScheduleEarnedTime:
                profile.DefaultAccessPolicy = ProfileAccessPolicy.Free;
                profile.ScheduledAccessPolicy = profile.DailyAllowanceMinutes > 0
                    ? ProfileAccessPolicy.AllowanceThenEarned
                    : ProfileAccessPolicy.EarnedTime;
                break;
            case BlockProfileMode.AlwaysBlock:
                profile.DefaultAccessPolicy = ProfileAccessPolicy.Block;
                profile.ScheduledAccessPolicy = ProfileAccessPolicy.Block;
                break;
            case BlockProfileMode.AllowanceThenEarned:
                profile.DefaultAccessPolicy = profile.DailyAllowanceMinutes > 0
                    ? ProfileAccessPolicy.AllowanceThenEarned
                    : ProfileAccessPolicy.EarnedTime;
                profile.ScheduledAccessPolicy = ProfileAccessPolicy.Block;
                break;
            case BlockProfileMode.EarnedTime:
            default:
                profile.DefaultAccessPolicy = ProfileAccessPolicy.EarnedTime;
                profile.ScheduledAccessPolicy = ProfileAccessPolicy.Block;
                break;
        }
    }

    private static bool IsScheduleActiveUnsafe(BlockProfile profile, DateTime localNow)
    {
        if (!profile.ScheduleEnabled || !BlockProfile.IsValidMask(profile.WeeklyScheduleMask)) return false;
        var dayIndex = (int)localNow.DayOfWeek;
        var slot = localNow.Hour * 2 + (localNow.Minute >= 30 ? 1 : 0);
        var index = dayIndex * 48 + slot;
        return index >= 0 && index < profile.WeeklyScheduleMask.Length &&
               profile.WeeklyScheduleMask[index] == '1';
    }

    private static string LegacyScheduleToMask(BlockProfile profile)
    {
        var chars = Enumerable.Repeat('0', 336).ToArray();
        if (!profile.ScheduleEnabled) return new string(chars);

        var days = ParseScheduleDays(profile.ScheduleDays);
        if (days.Count == 0 ||
            !TimeOnly.TryParse(profile.ScheduleStart, out var start) ||
            !TimeOnly.TryParse(profile.ScheduleEnd, out var end) ||
            start == end)
            return new string(chars);

        foreach (var day in days)
        {
            for (var slot = 0; slot < 48; slot++)
            {
                var minute = slot * 30;
                var time = new TimeOnly(minute / 60, minute % 60);
                if (start < end)
                {
                    if (time >= start && time < end)
                        chars[(int)day * 48 + slot] = '1';
                }
                else
                {
                    if (time >= start)
                        chars[(int)day * 48 + slot] = '1';

                    var next = ((int)day + 1) % 7;
                    if (time < end)
                        chars[next * 48 + slot] = '1';
                }
            }
        }
        return new string(chars);
    }

    private static readonly Dictionary<string, DayOfWeek> ScheduleDayMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sun"] = DayOfWeek.Sunday,
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday
    };

    private static HashSet<DayOfWeek> ParseScheduleDays(string? days)
    {
        var result = new HashSet<DayOfWeek>();
        foreach (var token in (days ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ScheduleDayMap.TryGetValue(token, out var day)) result.Add(day);
        return result;
    }

    private string AddBrowserRule(BrowserRule? rule)
    {
        if (rule is null) throw new InvalidOperationException("Thiếu browser rule.");
        rule.Pattern = NormalizeBrowserPattern(rule.Pattern, rule.MatchType);
        if (string.IsNullOrWhiteSpace(rule.Pattern)) throw new InvalidOperationException("Pattern browser không hợp lệ.");
        if (_state.BrowserRules.Any(r => r.MatchType == rule.MatchType && string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Rule này đã tồn tại.");
        rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id;
        rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : TrimTo(rule.Name.Trim(), 120);
        rule.Enabled = true;
        if (rule.Category == AppCategory.Entertainment)
        {
            var profile = GetDefaultBlockProfileUnsafe();
            rule.BlockProfileId = profile.Id;
            rule.BlockProfileName = profile.Name;
        }
        else
        {
            rule.BlockProfileId = "";
            rule.BlockProfileName = "";
        }
        _state.BrowserRules.Add(rule);
        AddAudit("Browser", $"Thêm rule {rule.DisplayName} → {rule.CategoryLabel} ({rule.MatchTypeLabel}).");
        _store.Save(_state);
        return "Đã thêm browser rule.";
    }

    private string SetBrowserProfile(string? ruleId, string? profileId)
    {
        var rule = _state.BrowserRules.FirstOrDefault(r => r.Id == ruleId) ?? throw new InvalidOperationException("Không tìm thấy website rule.");
        if (rule.Category != AppCategory.Entertainment) throw new InvalidOperationException("Profile chỉ áp dụng cho website giải trí.");
        var profile = _state.BlockProfiles.FirstOrDefault(p => p.Id == profileId) ?? throw new InvalidOperationException("Không tìm thấy profile.");
        rule.BlockProfileId = profile.Id;
        rule.BlockProfileName = profile.Name;
        AddAudit("Browser", $"{rule.DisplayName}: gán vào profile {profile.Name}.");
        _store.Save(_state);
        return $"Đã gán {rule.DisplayName} vào {profile.Name}.";
    }

    private string RemoveBrowserRule(string? id)
    {
        var rule = _state.BrowserRules.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("Không tìm thấy browser rule.");
        _state.BrowserRules.Remove(rule);
        AddAudit("Browser", $"Xóa browser rule {rule.DisplayName}.");
        _store.Save(_state);
        return "Đã xóa browser rule.";
    }

    private string ToggleBrowserRule(string? id)
    {
        var rule = _state.BrowserRules.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("Không tìm thấy browser rule.");
        rule.Enabled = !rule.Enabled;
        AddAudit("Browser", $"{(rule.Enabled ? "Bật" : "Tắt")} browser rule {rule.DisplayName}.");
        _store.Save(_state);
        return rule.Enabled ? "Đã bật browser rule." : "Đã tắt browser rule.";
    }

    private string CycleBrowserProfile(string? id)
    {
        var rule = _state.BrowserRules.FirstOrDefault(r => r.Id == id)
                   ?? throw new InvalidOperationException("Không tìm thấy browser rule.");
        if (rule.Category != AppCategory.Entertainment)
            throw new InvalidOperationException("Block Profile chỉ áp dụng cho website giải trí.");
        var profiles = _state.BlockProfiles.OrderBy(p => p.CreatedUtc).ToList();
        if (profiles.Count == 0) profiles.Add(GetDefaultBlockProfileUnsafe());
        var index = profiles.FindIndex(p => p.Id == rule.BlockProfileId);
        var next = profiles[(index + 1 + profiles.Count) % profiles.Count];
        rule.BlockProfileId = next.Id;
        rule.BlockProfileName = next.Name;
        AddAudit("Browser", $"{rule.DisplayName}: chuyển sang profile {next.Name}.");
        _store.Save(_state);
        return $"{rule.DisplayName}: profile {next.Name}.";
    }

    private static bool IsBrowserRulePolicyEnabledUnsafe(BrowserRule rule) => rule.Enabled;

    private static string NormalizeBrowserPattern(string? pattern, BrowserRuleMatchType matchType)
    {
        pattern = (pattern ?? "").Trim();
        if (matchType == BrowserRuleMatchType.HostSuffix)
        {
            if (Uri.TryCreate(pattern, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)) pattern = uri.Host;
            pattern = pattern.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
        }
        return TrimTo(pattern, matchType == BrowserRuleMatchType.TitleContains ? 256 : 2048);
    }

    private string UpdateSettings(UserSettings? settings)
    {
        if (settings is null) throw new InvalidOperationException("Thiếu cài đặt.");
        ValidateSettings(settings);
        _state.Settings = settings;
        var max = settings.MaxEntertainmentMinutes * 60;
        _state.EntertainmentBalanceSeconds = Math.Min(_state.EntertainmentBalanceSeconds, max);
        PruneStatisticsUnsafe();
        AddAudit("Config", "Đã cập nhật cài đặt.");
        _store.Save(_state);
        return "Đã lưu cài đặt.";
    }

    private string Redeem(string? code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        var key = _state.Keys.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.OrdinalIgnoreCase));
        if (key is null) throw new InvalidOperationException("Key không tồn tại.");
        if (key.Revoked) throw new InvalidOperationException("Key đã bị thu hồi.");
        if (key.IsRedeemed) throw new InvalidOperationException("Key đã được sử dụng.");
        if (key.IsExpired) throw new InvalidOperationException("Key đã hết hạn.");
        if (!_store.VerifyKey(key))
        {
            key.Revoked = true;
            AddAudit("Security", $"Từ chối key {key.Code}: chữ ký không hợp lệ.");
            _store.Save(_state);
            throw new InvalidOperationException("Key không hợp lệ hoặc dữ liệu đã bị sửa.");
        }

        var max = Math.Max(1, _state.Settings.MaxEntertainmentMinutes) * 60;
        var before = _state.EntertainmentBalanceSeconds;
        _state.EntertainmentBalanceSeconds = Math.Min(max, before + key.RewardSeconds);
        key.RedeemedUtc = DateTime.UtcNow;
        var added = _state.EntertainmentBalanceSeconds - before;
        var daily = GetDailyStatUnsafe(DateTime.Now.Date);
        daily.KeysRedeemed++;
        daily.RewardSecondsGranted += added;
        AddAudit("Reward", $"Đã dùng key {key.Code}, cộng {added} giây.");
        _store.Save(_state);
        return added > 0 ? $"Đã cộng {TimeSpan.FromSeconds(added):mm\\:ss}." : "Ví đã đạt giới hạn tối đa.";
    }

    private string ImportLegacy(AppState? legacy)
    {
        if (legacy is null) throw new InvalidOperationException("Không có dữ liệu MVP để nhập.");
        if (_state.Apps.Count > 0) return "FocusLock đã có cấu hình; bỏ qua nhập MVP.";

        _state.Apps = legacy.Apps.Select(Clone).ToList();
        var defaultProfile = GetDefaultBlockProfileUnsafe();
        foreach (var app in _state.Apps.Where(a => a.Category == AppCategory.Entertainment))
        {
            app.BlockProfileId = defaultProfile.Id;
            app.BlockProfileName = defaultProfile.Name;
        }
        _state.Settings.FocusMinutesPerKey = legacy.Settings.FocusMinutesPerKey;
        _state.Settings.RewardMinutesPerKey = legacy.Settings.RewardMinutesPerKey;
        _state.Settings.KeyExpiryMinutes = Math.Max(UserSettings.MinimumKeyExpiryMinutes, legacy.Settings.KeyExpiryMinutes);
        _state.Settings.IdleThresholdSeconds = legacy.Settings.IdleThresholdSeconds;
        _state.Settings.MaxEntertainmentMinutes = legacy.Settings.MaxEntertainmentMinutes;
        _state.Settings.BubbleEnabled = legacy.Settings.BubbleEnabled;
        _state.Settings.OnboardingCompleted = _state.Apps.Count > 0;
        _state.FocusProgressSeconds = Math.Max(0, legacy.FocusProgressSeconds);
        _state.EntertainmentBalanceSeconds = Math.Min(Math.Max(0, legacy.EntertainmentBalanceSeconds), _state.Settings.MaxEntertainmentMinutes * 60);
        _state.TotalFocusSeconds = Math.Max(0, legacy.TotalFocusSeconds);
        _state.TotalEntertainmentSeconds = Math.Max(0, legacy.TotalEntertainmentSeconds);
        _state.Keys.Clear(); // key MVP không có chữ ký HMAC nên cố ý không nhập.
        AddAudit("Migration", "Đã nhập cấu hình/thời gian từ MVP; key cũ không được nhập vì chưa có chữ ký HMAC.");
        _store.Save(_state);
        return "Đã nhập dữ liệu MVP. Key cũ không được chuyển sang V5.";
    }

    private ServiceSnapshot BuildSnapshotUnsafe()
    {
        var json = JsonSerializer.Serialize(_state);
        var clone = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        return new ServiceSnapshot
        {
            ServiceOnline = true,
            ServiceStatus = _state.ClockRollbackDetected ? "Guard đang khóa do thay đổi giờ" : _state.IntegrityIssueDetected ? "Guard đang chạy · đã khôi phục dữ liệu backup" : "Guard đang chạy",
            CurrentMode = _currentMode,
            CurrentApp = _currentApp,
            IsIdle = _isIdle,
            ActivityEventsLastMinute = _activityEvents.Count,
            HeartbeatHealthy = HeartbeatHealthyUnsafe(),
            InputMonitorHealthy = _inputMonitorHealthy,
            BrowserBridgeHealthy = BrowserBridgeHealthyUnsafe(),
            CurrentBrowser = _browserContext is null ? "—" : BrowserDisplayName(_browserContext.Browser),
            CurrentBrowserHost = _browserContext?.Host ?? "—",
            CurrentBrowserTitle = _browserContext?.Title ?? "—",
            CurrentBrowserUrl = _browserContext?.Url ?? "",
            CurrentBrowserCategory = _currentBrowserCategory,
            CurrentBrowserRule = _currentBrowserRule,
            CurrentBrowserBlocked = _currentBrowserBlocked,
            CurrentBrowserProfile = _currentBrowserProfile,
            CurrentBrowserAccess = _currentBrowserAccess,
            CurrentBrowserAllowanceRemainingSeconds = _currentBrowserAllowanceRemainingSeconds,
            BrowserForegroundActive = _browserContext is not null && BrowserForegroundActiveUnsafe(_browserContext.Browser),
            EntertainmentSessionActive = _entertainmentSessionActive,
            EntertainmentAccessMode = AccessModeShortLabel(_currentEntertainmentAccess),
            EntertainmentProfileName = _currentEntertainmentProfile,
            EntertainmentAllowanceRemainingSeconds = _currentEntertainmentAllowanceRemainingSeconds,
            EntertainmentWalletRemainingSeconds = _state.EntertainmentBalanceSeconds,
            EntertainmentUsableRemainingSeconds = CurrentEntertainmentUsableRemainingSecondsUnsafe(),
            BrowserDocumentVisible = _browserContext?.DocumentVisible == true,
            BrowserMediaPlaying = _browserContext?.MediaPlaying == true,
            BrowserMediaProgressing = _browserContext?.MediaProgressing == true,
            BrowserFocusQualified = _currentBrowserCategory == "Focus" && _browserContext is not null &&
                                    BrowserForegroundActiveUnsafe(_browserContext.Browser) &&
                                    _browserContext.DocumentVisible && BrowserFocusQualifiedUnsafe(DateTime.UtcNow),
            BrowserActivityEventsLastMinute = _browserActivityEvents.Count,
            State = clone,
            Analytics = BuildAnalyticsUnsafe(),
            SnapshotUtc = DateTime.UtcNow
        };
    }

    private bool HeartbeatHealthyUnsafe()
    {
        if (_lastHeartbeatTick == 0) return false;
        var age = (Stopwatch.GetTimestamp() - _lastHeartbeatTick) / (double)Stopwatch.Frequency;
        return age <= Math.Max(2, _state.Settings.AgentHeartbeatTimeoutSeconds);
    }

    private bool BrowserBridgeHealthyUnsafe()
    {
        if (_lastBrowserContextTick == 0) return false;
        var age = (Stopwatch.GetTimestamp() - _lastBrowserContextTick) / (double)Stopwatch.Frequency;
        return age <= Math.Clamp(Math.Max(12, _state.Settings.BrowserContextTimeoutSeconds), 12, 30);
    }

    private void CheckClock(DateTime now)
    {
        var tolerance = TimeSpan.FromSeconds(Math.Max(30, _state.Settings.ClockRollbackToleranceSeconds));
        if (now + tolerance < _state.LastSeenUtc)
        {
            if (!_state.ClockRollbackDetected)
            {
                _state.ClockRollbackDetected = true;
                _state.EntertainmentBalanceSeconds = 0;
                AddAudit("Security", $"Phát hiện giờ hệ thống lùi từ {_state.LastSeenUtc:O} về {now:O}. Ví giải trí đã bị khóa.");
            }
            return;
        }

        if (_state.ClockRollbackDetected && now + tolerance >= _state.LastSeenUtc)
        {
            _state.ClockRollbackDetected = false;
            AddAudit("Security", "Giờ hệ thống đã trở lại phạm vi hợp lệ; gỡ cảnh báo clock rollback.");
        }
        _state.LastSeenUtc = Max(_state.LastSeenUtc, now);
    }

    private void RecordUsageSecondUnsafe(TrackedApp app, AppCategory category)
    {
        var localDate = DateTime.Now.Date;
        var day = GetDailyStatUnsafe(localDate);
        if (category == AppCategory.Focus) day.FocusSeconds++;
        else day.EntertainmentSeconds++;

        var dateKey = ToDateKey(localDate);
        var appStat = _state.AppUsage.FirstOrDefault(x => x.DateKey == dateKey && x.AppId == app.Id && x.Category == category);
        if (appStat is null)
        {
            appStat = new AppUsageStat
            {
                DateKey = dateKey,
                AppId = app.Id,
                AppName = app.Name,
                Category = category
            };
            _state.AppUsage.Add(appStat);
        }
        appStat.AppName = app.Name;
        appStat.ActiveSeconds++;

        var nowUtc = DateTime.UtcNow;
        if (_openSession is null || _openSession.AppId != app.Id || _openSession.Category != category ||
            (nowUtc - _openSession.LastActiveUtc).TotalSeconds > 3)
        {
            EndUsageSessionUnsafe("Chuyển ứng dụng/trạng thái");
            _openSession = new UsageSession
            {
                AppId = app.Id,
                AppName = app.Name,
                Category = category,
                StartedUtc = nowUtc,
                LastActiveUtc = nowUtc,
                ActiveSeconds = 0
            };
            _state.SessionHistory.Insert(0, _openSession);
        }

        _openSession.LastActiveUtc = nowUtc;
        _openSession.ActiveSeconds++;
        PruneStatisticsUnsafe();
    }

    private void EndUsageSessionUnsafe(string reason)
    {
        if (_openSession is null) return;
        _openSession.EndedUtc = _openSession.LastActiveUtc == default ? DateTime.UtcNow : _openSession.LastActiveUtc;
        _openSession.EndReason = reason;
        _openSession = null;
    }

    private DailyUsageStat GetDailyStatUnsafe(DateTime localDate)
    {
        var key = ToDateKey(localDate);
        var stat = _state.DailyUsage.FirstOrDefault(x => x.DateKey == key);
        if (stat is null)
        {
            stat = new DailyUsageStat { DateKey = key };
            _state.DailyUsage.Add(stat);
        }
        return stat;
    }

    private AnalyticsSnapshot BuildAnalyticsUnsafe()
    {
        var today = DateTime.Now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);

        return new AnalyticsSnapshot
        {
            Today = BuildPeriodUnsafe("Hôm nay", today, today),
            Week = BuildPeriodUnsafe("Tuần này", weekStart, today),
            Month = BuildPeriodUnsafe("Tháng này", monthStart, today),
            CurrentStreakDays = CalculateCurrentStreakUnsafe(today),
            BestStreakDays = CalculateBestStreakUnsafe(),
            StreakGoalMinutes = Math.Max(1, _state.Settings.StreakMinimumFocusMinutes),
            Last7Days = Enumerable.Range(0, 7).Select(offset =>
            {
                var date = today.AddDays(offset - 6);
                var stat = _state.DailyUsage.FirstOrDefault(x => x.DateKey == ToDateKey(date));
                return new DailyChartPoint
                {
                    DateKey = ToDateKey(date),
                    DayLabel = date.ToString("ddd dd/MM"),
                    FocusSeconds = stat?.FocusSeconds ?? 0,
                    EntertainmentSeconds = stat?.EntertainmentSeconds ?? 0
                };
            }).ToList()
        };
    }

    private PeriodAnalytics BuildPeriodUnsafe(string label, DateTime start, DateTime end)
    {
        var startKey = ToDateKey(start);
        var endKey = ToDateKey(end);
        var days = _state.DailyUsage.Where(x => string.CompareOrdinal(x.DateKey, startKey) >= 0 && string.CompareOrdinal(x.DateKey, endKey) <= 0).ToList();
        var focus = days.Sum(x => x.FocusSeconds);
        var play = days.Sum(x => x.EntertainmentSeconds);
        var total = focus + play;
        var appRows = _state.AppUsage
            .Where(x => string.CompareOrdinal(x.DateKey, startKey) >= 0 && string.CompareOrdinal(x.DateKey, endKey) <= 0)
            .GroupBy(x => new { x.AppId, x.AppName, x.Category })
            .Select(g => new AppAnalyticsRow
            {
                AppId = g.Key.AppId,
                AppName = g.Key.AppName,
                Category = g.Key.Category == AppCategory.Focus ? "Focus" : "Giải trí",
                Seconds = g.Sum(x => x.ActiveSeconds)
            })
            .OrderByDescending(x => x.Seconds)
            .ToList();

        var startUtc = DateTime.SpecifyKind(start, DateTimeKind.Local).ToUniversalTime();
        var endUtcExclusive = DateTime.SpecifyKind(end.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        var keysGenerated = _state.Keys.Count(k => k.CreatedUtc >= startUtc && k.CreatedUtc < endUtcExclusive);
        var keysRedeemed = _state.Keys.Count(k => k.RedeemedUtc.HasValue && k.RedeemedUtc.Value >= startUtc && k.RedeemedUtc.Value < endUtcExclusive);
        var keysExpired = _state.Keys.Count(k => k.ExpiresUtc >= startUtc && k.ExpiresUtc < endUtcExclusive && k.IsExpired && !k.IsRedeemed && !k.Revoked);

        return new PeriodAnalytics
        {
            Label = label,
            FocusSeconds = focus,
            EntertainmentSeconds = play,
            SuspiciousSeconds = days.Sum(x => x.SuspiciousSeconds),
            KeysGenerated = keysGenerated,
            KeysRedeemed = keysRedeemed,
            KeysExpired = keysExpired,
            RewardSecondsGranted = days.Sum(x => x.RewardSecondsGranted),
            FocusPercent = total <= 0 ? 0 : focus * 100.0 / total,
            EntertainmentPercent = total <= 0 ? 0 : play * 100.0 / total,
            Apps = appRows
        };
    }

    private int CalculateCurrentStreakUnsafe(DateTime today)
    {
        var goal = Math.Max(1, _state.Settings.StreakMinimumFocusMinutes) * 60L;
        var lookup = _state.DailyUsage.ToDictionary(x => x.DateKey, x => x.FocusSeconds, StringComparer.Ordinal);
        var cursor = today;
        if (!lookup.TryGetValue(ToDateKey(today), out var todayFocus) || todayFocus < goal)
            cursor = today.AddDays(-1); // hôm nay chưa đạt mục tiêu thì chưa làm đứt streak đang có.

        var count = 0;
        while (lookup.TryGetValue(ToDateKey(cursor), out var seconds) && seconds >= goal)
        {
            count++;
            cursor = cursor.AddDays(-1);
        }
        return count;
    }

    private int CalculateBestStreakUnsafe()
    {
        var goal = Math.Max(1, _state.Settings.StreakMinimumFocusMinutes) * 60L;
        var qualified = _state.DailyUsage
            .Where(x => x.FocusSeconds >= goal && DateTime.TryParseExact(x.DateKey, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
            .Select(x => DateTime.ParseExact(x.DateKey, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (qualified.Count == 0) return 0;

        var best = 1;
        var current = 1;
        for (var i = 1; i < qualified.Count; i++)
        {
            if ((qualified[i] - qualified[i - 1]).Days == 1) current++;
            else current = 1;
            if (current > best) best = current;
        }
        return best;
    }

    private void PruneStatisticsUnsafe()
    {
        var retention = Math.Clamp(_state.Settings.StatisticsRetentionDays, 30, 3650);
        var cutoff = DateTime.Now.Date.AddDays(-retention + 1);
        var cutoffKey = ToDateKey(cutoff);
        _state.DailyUsage.RemoveAll(x => string.CompareOrdinal(x.DateKey, cutoffKey) < 0);
        _state.AppUsage.RemoveAll(x => string.CompareOrdinal(x.DateKey, cutoffKey) < 0);

        var limit = Math.Clamp(_state.Settings.SessionHistoryLimit, 100, 20000);
        if (_state.SessionHistory.Count > limit)
            _state.SessionHistory.RemoveRange(limit, _state.SessionHistory.Count - limit);
    }

    private static string ToDateKey(DateTime date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private string GetCachedHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (_hashCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Hash;
            var hash = ProcessTools.TrySha256(path);
            _hashCache[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch { return ""; }
    }

    private void NormalizeState()
    {
        var previousSchema = _state.SchemaVersion;
        _state.Apps ??= new();
        _state.Keys ??= new();
        _state.AuditLog ??= new();
        _state.Settings ??= new();
        // Existing V1-V5 users should not be forced through the first-run wizard after upgrade.
        if (previousSchema < 7 && (_state.Apps.Count > 0 || _state.Keys.Count > 0 || _state.TotalFocusSeconds > 0 || _state.TotalEntertainmentSeconds > 0))
            _state.Settings.OnboardingCompleted = true;
        _state.SchemaVersion = 12;
        _state.DailyUsage ??= new();
        _state.AppUsage ??= new();
        _state.SessionHistory ??= new();
        _state.BrowserRules ??= new();
        _state.BlockProfiles ??= new();
        _state.ControlPolicy ??= new();
        _state.ControlPolicy.StrictUnlockDelayMinutes = Math.Clamp(_state.ControlPolicy.StrictUnlockDelayMinutes <= 0 ? 30 : _state.ControlPolicy.StrictUnlockDelayMinutes, 1, 1440);
        if (_state.ControlPolicy.SettingsProtectionMode == SettingsProtectionMode.TypingChallenge &&
            string.IsNullOrWhiteSpace(_state.ControlPolicy.SettingsUnlockChallenge))
            _state.ControlPolicy.SettingsProtectionMode = SettingsProtectionMode.Off;
        if (_state.ControlPolicy.SettingsProtectionMode == SettingsProtectionMode.TimeWindow &&
            (_state.ControlPolicy.SettingsProtectionStartUtc is null || _state.ControlPolicy.SettingsProtectionUntilUtc is null))
            _state.ControlPolicy.SettingsProtectionMode = SettingsProtectionMode.Off;
        if (_state.BlockProfiles.Count == 0)
            _state.BlockProfiles.Add(new BlockProfile { Name = "Giải trí chung", Enabled = true });
        var defaultProfile = _state.BlockProfiles[0];
        foreach (var profile in _state.BlockProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.ScheduleDays)) profile.ScheduleDays = "Mon,Tue,Wed,Thu,Fri";
            if (string.IsNullOrWhiteSpace(profile.ScheduleStart)) profile.ScheduleStart = "08:00";
            if (string.IsNullOrWhiteSpace(profile.ScheduleEnd)) profile.ScheduleEnd = "12:00";

            if (!BlockProfile.IsValidMask(profile.WeeklyScheduleMask))
                profile.WeeklyScheduleMask = LegacyScheduleToMask(profile);

            if (profile.PolicyVersion < 2)
            {
                MigrateLegacyProfilePolicyUnsafe(profile);
                profile.PolicyVersion = 2;
            }

            // Core FocusLock expectation: the built-in "Giải trí chung" profile
            // should consume earned Focus time by default. Older V7.1/V7.2
            // schedule profiles could migrate to Free outside the calendar,
            // which made entertainment websites look as if the wallet was broken.
            if (previousSchema < 10 &&
                string.Equals(profile.Name, "Giải trí chung", StringComparison.OrdinalIgnoreCase) &&
                profile.DefaultAccessPolicy == ProfileAccessPolicy.Free)
            {
                profile.DefaultAccessPolicy = profile.DailyAllowanceMinutes > 0
                    ? ProfileAccessPolicy.AllowanceThenEarned
                    : ProfileAccessPolicy.EarnedTime;
            }

            profile.DailyAllowanceMinutes = Math.Clamp(profile.DailyAllowanceMinutes, 0, 1440);
            ResetAllowanceIfNewDayUnsafe(profile, DateTime.Now);
        }
        foreach (var app in _state.Apps.Where(a => a.Category == AppCategory.Entertainment))
        {
            if (string.IsNullOrWhiteSpace(app.BlockProfileId) || !_state.BlockProfiles.Any(p => p.Id == app.BlockProfileId))
            {
                app.BlockProfileId = defaultProfile.Id;
                app.BlockProfileName = defaultProfile.Name;
            }
            else
            {
                app.BlockProfileName = _state.BlockProfiles.First(p => p.Id == app.BlockProfileId).Name;
            }
            if (previousSchema < 10)
            {
                var p = _state.BlockProfiles.FirstOrDefault(x => x.Id == app.BlockProfileId);
                app.UseCustomBlockAction = p is null || !p.OverrideAppBlockAction;
            }
        }
        foreach (var rule in _state.BrowserRules.Where(r => r.Category == AppCategory.Entertainment))
        {
            if (string.IsNullOrWhiteSpace(rule.BlockProfileId) || !_state.BlockProfiles.Any(p => p.Id == rule.BlockProfileId))
            {
                rule.BlockProfileId = defaultProfile.Id;
                rule.BlockProfileName = defaultProfile.Name;
            }
            else
            {
                rule.BlockProfileName = _state.BlockProfiles.First(p => p.Id == rule.BlockProfileId).Name;
            }
        }
        foreach (var rule in _state.BrowserRules.Where(r => r.Category == AppCategory.Focus))
        {
            rule.BlockProfileId = "";
            rule.BlockProfileName = "";
        }
        // V7.4: reward keys must live for at least 24 hours. Upgrade existing
        // short-lived unredeemed keys and re-sign them because expiry is part of the HMAC.
        _state.Settings.KeyExpiryMinutes = Math.Max(UserSettings.MinimumKeyExpiryMinutes, _state.Settings.KeyExpiryMinutes);
        foreach (var key in _state.Keys)
        {
            if (key.CreatedUtc == default) key.CreatedUtc = DateTime.UtcNow;
            var minimumExpiry = key.CreatedUtc.AddMinutes(UserSettings.MinimumKeyExpiryMinutes);
            if (!key.IsRedeemed && !key.Revoked && key.ExpiresUtc < minimumExpiry)
            {
                key.ExpiresUtc = minimumExpiry;
                key.Signature = _store.SignKey(key);
            }
            else if (key.ExpiresUtc <= key.CreatedUtc)
            {
                key.ExpiresUtc = key.CreatedUtc.AddMinutes(Math.Max(UserSettings.MinimumKeyExpiryMinutes, _state.Settings.KeyExpiryMinutes));
                key.Signature = _store.SignKey(key);
            }
        }

        if (_state.BrowserRules.Count == 0 && _state.Apps.Count == 0 && _state.Keys.Count == 0 &&
            _state.TotalFocusSeconds == 0 && _state.TotalEntertainmentSeconds == 0)
        {
            _state.BrowserRules.AddRange(new[]
            {
                new BrowserRule { Name = "YouTube", Pattern = "youtube.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment, BlockProfileId = defaultProfile.Id, BlockProfileName = defaultProfile.Name },
                new BrowserRule { Name = "Netflix", Pattern = "netflix.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment, BlockProfileId = defaultProfile.Id, BlockProfileName = defaultProfile.Name },
                new BrowserRule { Name = "Facebook", Pattern = "facebook.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment, BlockProfileId = defaultProfile.Id, BlockProfileName = defaultProfile.Name },
                new BrowserRule { Name = "TikTok", Pattern = "tiktok.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment, BlockProfileId = defaultProfile.Id, BlockProfileName = defaultProfile.Name },
                new BrowserRule { Name = "Coursera", Pattern = "coursera.org", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Focus },
                new BrowserRule { Name = "Khan Academy", Pattern = "khanacademy.org", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Focus },
                new BrowserRule { Name = "Google Docs", Pattern = "docs.google.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Focus }
            });
            AddAudit("Browser", "Đã tạo bộ browser rule mẫu V5 cho cài đặt mới.");
        }
        foreach (var session in _state.SessionHistory.Where(x => x.EndedUtc is null))
        {
            session.EndedUtc = session.LastActiveUtc == default ? DateTime.UtcNow : session.LastActiveUtc;
            session.EndReason = "Service restart";
        }
        PruneStatisticsUnsafe();
        _state.EntertainmentBalanceSeconds = Math.Max(0, _state.EntertainmentBalanceSeconds);
        _state.FocusProgressSeconds = Math.Max(0, _state.FocusProgressSeconds);
        foreach (var app in _state.Apps)
            if (string.IsNullOrWhiteSpace(app.Id)) app.Id = Guid.NewGuid().ToString("N");
    }

    private void AddAudit(string type, string message)
    {
        _state.AuditLog.Insert(0, new AuditEvent { AtUtc = DateTime.UtcNow, Type = type, Message = message });
        if (_state.AuditLog.Count > 500) _state.AuditLog.RemoveRange(500, _state.AuditLog.Count - 500);
    }

    private static void ValidateSettings(UserSettings s)
    {
        if (s.FocusMinutesPerKey <= 0 || s.RewardMinutesPerKey <= 0 || s.IdleThresholdSeconds <= 0 || s.MaxEntertainmentMinutes <= 0)
            throw new InvalidOperationException("Các thông số thời gian phải là số nguyên dương.");
        if (s.KeyExpiryMinutes < UserSettings.MinimumKeyExpiryMinutes)
            throw new InvalidOperationException("Thời hạn phần thưởng tối thiểu là 24 giờ.");
        if (s.MinimumActivityEventsPerMinute < 1 || s.MinimumActivityEventsPerMinute > 60)
            throw new InvalidOperationException("Activity events/phút phải từ 1 đến 60.");
        if (s.AgentHeartbeatTimeoutSeconds < 2 || s.AgentHeartbeatTimeoutSeconds > 60)
            throw new InvalidOperationException("Heartbeat timeout phải từ 2 đến 60 giây.");
        if (s.StreakMinimumFocusMinutes < 1 || s.StreakMinimumFocusMinutes > 1440)
            throw new InvalidOperationException("Mục tiêu streak phải từ 1 đến 1440 phút/ngày.");
        if (s.StatisticsRetentionDays < 30 || s.StatisticsRetentionDays > 3650)
            throw new InvalidOperationException("Lưu thống kê phải từ 30 đến 3650 ngày.");
        if (s.SessionHistoryLimit < 100 || s.SessionHistoryLimit > 20000)
            throw new InvalidOperationException("Giới hạn session phải từ 100 đến 20000.");
        if (s.BrowserContextTimeoutSeconds < 2 || s.BrowserContextTimeoutSeconds > 30)
            throw new InvalidOperationException("Browser context timeout phải từ 2 đến 30 giây.");
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static TrackedApp Clone(TrackedApp a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        ExePath = a.ExePath,
        ProcessName = a.ProcessName,
        Sha256 = a.Sha256,
        Category = a.Category,
        Enabled = a.Enabled,
        BlockAction = a.BlockAction,
        UseCustomBlockAction = a.UseCustomBlockAction,
        BlockProfileId = a.BlockProfileId,
        BlockProfileName = a.BlockProfileName
    };
}
