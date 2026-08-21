using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusLock.Shared.Models;

namespace FocusLock.Service.Services;

public sealed class SecureStateStore
{
    private sealed class Envelope
    {
        public string Payload { get; set; } = "";
        public string Hmac { get; set; } = "";
    }

    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private readonly string _directory;
    private readonly string _file;
    private readonly string _backup;
    private readonly string _secretFile;
    private readonly byte[] _secret;

    public SecureStateStore()
    {
        // Code-folder mode: runtime data lives beside App/Service/NativeHost/BrowserExtension.
        // Expected installed layout:
        //   <FocusLockRoot>\Service\FocusLock.Service.exe
        //   <FocusLockRoot>\Data\state.v2.json
        var root = ResolveFocusLockRoot();
        _directory = Path.Combine(root, "Data");
        _file = Path.Combine(_directory, "state.v2.json");
        _backup = Path.Combine(_directory, "state.v2.bak");
        _secretFile = Path.Combine(_directory, "guard.secret");
        Directory.CreateDirectory(_directory);
        _secret = LoadOrCreateSecret();
    }

    public string DataDirectory => _directory;

    public AppState Load()
    {
        if (TryLoad(_file, out var state)) return state;
        if (TryLoad(_backup, out state))
        {
            state.IntegrityIssueDetected = true;
            AddAudit(state, "Integrity", "File dữ liệu chính không hợp lệ; đã khôi phục từ bản sao lưu.");
            return state;
        }

        var fresh = new AppState();
        if (File.Exists(_file) || File.Exists(_backup))
        {
            fresh.IntegrityIssueDetected = true;
            AddAudit(fresh, "Integrity", "Không xác minh được dữ liệu cũ; đã tạo trạng thái mới an toàn.");
        }
        return fresh;
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(_directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, _json);
        var envelope = new Envelope
        {
            Payload = Convert.ToBase64String(payload),
            Hmac = ComputeHmac(payload)
        };
        var serialized = JsonSerializer.Serialize(envelope, _json);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, serialized, Encoding.UTF8);

        if (File.Exists(_file) && TryLoad(_file, out _)) File.Copy(_file, _backup, overwrite: true);
        File.Move(temp, _file, overwrite: true);
    }

    public string SignKey(RewardKey key)
    {
        var canonical = $"{key.Id}|{key.Code}|{key.Nonce}|{key.CreatedUtc.Ticks}|{key.ExpiresUtc.Ticks}|{key.RewardSeconds}";
        using var hmac = new HMACSHA256(_secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    public bool VerifyKey(RewardKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Signature)) return false;
        var expected = SignKey(key);
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(key.Signature));
        }
        catch { return false; }
    }

    private static string ResolveFocusLockRoot()
    {
        // Optional override is useful for debugging, but normal installs do not need it.
        var configured = Environment.GetEnvironmentVariable("FOCUSLOCK_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var current = new DirectoryInfo(baseDir);

        // Published service normally lives in <root>\Service.
        if (current.Name.Equals("Service", StringComparison.OrdinalIgnoreCase) && current.Parent is not null)
            return current.Parent.FullName;

        // Fallback for development/debug layouts.
        return current.FullName;
    }

    private bool TryLoad(string path, out AppState state)
    {
        state = new AppState();
        try
        {
            if (!File.Exists(path)) return false;
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path, Encoding.UTF8), _json);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Hmac)) return false;
            var payload = Convert.FromBase64String(envelope.Payload);
            var actual = Convert.FromHexString(envelope.Hmac);
            var expected = Convert.FromHexString(ComputeHmac(payload));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
            state = JsonSerializer.Deserialize<AppState>(payload, _json) ?? new AppState();
            return true;
        }
        catch { return false; }
    }

    private byte[] LoadOrCreateSecret()
    {
        try
        {
            if (File.Exists(_secretFile))
            {
                var bytes = File.ReadAllBytes(_secretFile);
                if (bytes.Length >= 32) return bytes;
            }
        }
        catch { }

        var secret = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(_secretFile, secret);
        try { File.SetAttributes(_secretFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
        return secret;
    }

    private string ComputeHmac(byte[] payload)
    {
        using var hmac = new HMACSHA256(_secret);
        return Convert.ToHexString(hmac.ComputeHash(payload));
    }

    private static void AddAudit(AppState state, string type, string message)
    {
        state.AuditLog.Insert(0, new AuditEvent { Type = type, Message = message });
        if (state.AuditLog.Count > 500) state.AuditLog.RemoveRange(500, state.AuditLog.Count - 500);
    }
}
