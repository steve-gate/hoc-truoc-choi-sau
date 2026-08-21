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
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc, string Hash)> _hashCache = new(StringComparer.OrdinalIgnoreCase);
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

    public FocusAuthorityEngine(SecureStateStore store)
    {
        _store = store;
        _state = store.Load();
        NormalizeState();
        AddAudit("Service", "FocusLock Guard V5 khởi động · analytics + browser bridge enabled.");
    }

    public PipeResponse Handle(PipeRequest request)
    {
        lock (_gate)
        {
            try
            {
                BrowserDecision? browserDecision = null;
                string message;
                switch (request.Command.ToLowerInvariant())
                {
                    case "activity": message = HandleActivityCommand(request.Activity); break;
                    case "addapp": message = AddApp(request.App); break;
                    case "removeapp": message = RemoveApp(request.AppId); break;
                    case "toggleapp": message = ToggleApp(request.AppId); break;
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
            if (!HeartbeatHealthyUnsafe())
            {
                _currentMode = "Agent mất kết nối – khóa an toàn";
                _currentApp = "—";
                EndUsageSessionUnsafe("Mất heartbeat");
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

    public void EnforceEntertainmentLock()
    {
        List<TrackedApp> targets;
        bool verifyHash;
        lock (_gate)
        {
            targets = _state.Apps.Where(a => a.Enabled && a.Category == AppCategory.Entertainment).Select(Clone).ToList();
            verifyHash = _state.Settings.VerifyExecutableHash;
        }
        if (targets.Count == 0) return;

        var targetHashes = targets.Where(t => !string.IsNullOrWhiteSpace(t.Sha256))
            .Select(t => t.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = ProcessTools.TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path)) continue;
                var processName = process.ProcessName;

                var directMatch = targets.Any(t =>
                    string.Equals(t.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(t.ExePath) || PathsEqual(t.ExePath, path)));

                var hashMatch = false;
                if (!directMatch && verifyHash && targetHashes.Count > 0)
                {
                    var hash = GetCachedHash(path);
                    hashMatch = !string.IsNullOrWhiteSpace(hash) && targetHashes.Contains(hash);
                }

                if (directMatch || hashMatch) ProcessTools.TryKill(process);
            }
            catch { }
            finally { process.Dispose(); }
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
        if (actual is null)
        {
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
            _currentMode = _isIdle ? "Đang nghỉ" : "Ứng dụng không theo dõi";
            EndUsageSessionUnsafe("Ứng dụng không thuộc rule");
            return;
        }
        _currentApp = tracked.Name;

        if (_state.ClockRollbackDetected)
        {
            _currentMode = "Phát hiện thay đổi giờ hệ thống – đang khóa";
            EndUsageSessionUnsafe("Clock rollback");
            return;
        }

        var canCountOneSecond = gapSeconds > 0 && gapSeconds <= 2.5;
        if (tracked.Category == AppCategory.Focus)
        {
            if (_lastFocusAppId != tracked.Id)
            {
                _lastFocusAppId = tracked.Id;
                _activityEvents.Clear();
            }
            if (sample.InputChanged) _activityEvents.Enqueue(now);
            while (_activityEvents.Count > 0 && (now - _activityEvents.Peek()).TotalSeconds > 60) _activityEvents.Dequeue();

            if (_isIdle)
            {
                _currentMode = "Focus tạm dừng (idle)";
                EndUsageSessionUnsafe("Focus idle");
                return;
            }

            if (!FocusActivityQualifies())
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

            _currentMode = "Đang học / làm việc";
            if (canCountOneSecond) CreditFocusSecond(tracked);
            return;
        }

        _lastFocusAppId = "";
        if (_state.EntertainmentBalanceSeconds <= 0)
        {
            _currentMode = "Giải trí đang bị khóa";
            EndUsageSessionUnsafe("Hết balance");
            return;
        }

        if (_isIdle)
        {
            _currentMode = "Giải trí tạm dừng (idle)";
            EndUsageSessionUnsafe("Giải trí idle");
            return;
        }

        _currentMode = "Đang giải trí";
        if (canCountOneSecond)
        {
            _state.EntertainmentBalanceSeconds = Math.Max(0, _state.EntertainmentBalanceSeconds - 1);
            _state.TotalEntertainmentSeconds++;
            RecordUsageSecondUnsafe(tracked, AppCategory.Entertainment);
        }
    }

    private bool FocusActivityQualifies()
    {
        if (!_state.Settings.AntiCheatEnabled) return true;
        return _activityEvents.Count >= Math.Max(1, _state.Settings.MinimumActivityEventsPerMinute);
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
                Math.Max(1, _state.Settings.KeyExpiryMinutes),
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
        _browserContext = sample;
        _lastBrowserContextTick = Stopwatch.GetTimestamp();

        var rule = _state.Settings.BrowserRulesEnabled ? FindBrowserRuleUnsafe(sample.Url, sample.Title) : null;
        _currentBrowserCategory = rule is null ? "Neutral" : rule.Category == AppCategory.Focus ? "Focus" : "Giải trí";
        _currentBrowserRule = rule?.DisplayName ?? "—";
        _currentBrowserBlocked = rule?.Category == AppCategory.Entertainment &&
            (_state.EntertainmentBalanceSeconds <= 0 || _state.ClockRollbackDetected || !HeartbeatHealthyUnsafe());

        var message = rule is null
            ? "Website chưa có rule FocusLock."
            : _currentBrowserBlocked
                ? $"Đã khóa {rule.DisplayName}: không có thời gian giải trí khả dụng."
                : $"Đã phân loại {rule.DisplayName} → {_currentBrowserCategory}.";

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
            FocusProgressSeconds = _state.FocusProgressSeconds
        };
    }

    private TrackedApp? ResolveBrowserTrackedUnsafe(string processName, string path)
    {
        if (!_state.Settings.BrowserRulesEnabled || !BrowserBridgeHealthyUnsafe() || _browserContext is null || !_browserContext.WindowFocused)
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
            Enabled = true
        };
    }

    private BrowserRule? FindBrowserRuleUnsafe(string url, string title)
    {
        var host = ExtractHost(url);
        return _state.BrowserRules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern) && BrowserRuleMatches(r, url, title, host))
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
        foreach (var app in _state.Apps.Where(a => a.Enabled))
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
                var byHash = _state.Apps.FirstOrDefault(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Sha256) &&
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
        _state.BrowserRules.Add(rule);
        AddAudit("Browser", $"Thêm rule {rule.DisplayName} → {rule.CategoryLabel} ({rule.MatchTypeLabel}).");
        _store.Save(_state);
        return "Đã thêm browser rule.";
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
        _state.Settings.FocusMinutesPerKey = legacy.Settings.FocusMinutesPerKey;
        _state.Settings.RewardMinutesPerKey = legacy.Settings.RewardMinutesPerKey;
        _state.Settings.KeyExpiryMinutes = legacy.Settings.KeyExpiryMinutes;
        _state.Settings.IdleThresholdSeconds = legacy.Settings.IdleThresholdSeconds;
        _state.Settings.MaxEntertainmentMinutes = legacy.Settings.MaxEntertainmentMinutes;
        _state.Settings.BubbleEnabled = legacy.Settings.BubbleEnabled;
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
        return age <= Math.Clamp(_state.Settings.BrowserContextTimeoutSeconds, 2, 30);
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
        _state.SchemaVersion = 5;
        _state.Apps ??= new();
        _state.Keys ??= new();
        _state.AuditLog ??= new();
        _state.Settings ??= new();
        _state.DailyUsage ??= new();
        _state.AppUsage ??= new();
        _state.SessionHistory ??= new();
        _state.BrowserRules ??= new();
        if (_state.BrowserRules.Count == 0 && _state.Apps.Count == 0 && _state.Keys.Count == 0 &&
            _state.TotalFocusSeconds == 0 && _state.TotalEntertainmentSeconds == 0)
        {
            _state.BrowserRules.AddRange(new[]
            {
                new BrowserRule { Name = "YouTube", Pattern = "youtube.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment },
                new BrowserRule { Name = "Netflix", Pattern = "netflix.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment },
                new BrowserRule { Name = "Facebook", Pattern = "facebook.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment },
                new BrowserRule { Name = "TikTok", Pattern = "tiktok.com", MatchType = BrowserRuleMatchType.HostSuffix, Category = AppCategory.Entertainment },
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
        if (s.FocusMinutesPerKey <= 0 || s.RewardMinutesPerKey <= 0 || s.KeyExpiryMinutes <= 0 || s.IdleThresholdSeconds <= 0 || s.MaxEntertainmentMinutes <= 0)
            throw new InvalidOperationException("Các thông số thời gian phải là số nguyên dương.");
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
        Enabled = a.Enabled
    };
}
