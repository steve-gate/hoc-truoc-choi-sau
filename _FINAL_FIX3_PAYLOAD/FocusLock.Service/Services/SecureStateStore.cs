using System.IO.Compression;
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

    private sealed class BackupManifest
    {
        public string Product { get; set; } = "FocusLock";
        public int FormatVersion { get; set; } = 1;
        public string AppVersion { get; set; } = "7.8.0.2";
        public int SchemaVersion { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string StateSha256 { get; set; } = "";
        public string SecretSha256 { get; set; } = "";
    }

    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private readonly string _directory;
    private readonly string _file;
    private readonly string _backup;
    private readonly string _secretFile;
    private readonly string _secretBackupFile;
    private byte[] _secret;

    public SecureStateStore()
    {
        // Code-folder mode: persistence is anchored outside versioned OneDir folders.
        // Expected installed layout:
        //   <CodeRoot>\FocusLock-OneDir-*\Service\FocusLock.Service.exe
        //   <CodeRoot>\FocusLock-Data\Data\state.v2.json
        var root = ResolveFocusLockRoot();
        _directory = Path.Combine(root, "Data");
        _file = Path.Combine(_directory, "state.v2.json");
        _backup = Path.Combine(_directory, "state.v2.bak");
        _secretFile = Path.Combine(_directory, "guard.secret");
        _secretBackupFile = Path.Combine(_directory, "guard.secret.bak");
        Directory.CreateDirectory(_directory);
        _secret = LoadOrCreateSecret();
    }

    public string DataDirectory => _directory;

    public AppState Load()
    {
        var hasPrimarySecret = TryReadSecretBytes(_secretFile, out var primarySecret);
        var hasBackupSecret = TryReadSecretBytes(_secretBackupFile, out var backupSecret);

        // Always prefer the newest state file, but validate it against BOTH known secret
        // candidates. A readable primary secret is not automatically trustworthy: an old
        // build may have rotated it while state.v2.json was still signed with guard.secret.bak.
        if (hasPrimarySecret && TryLoadWithSecret(_file, primarySecret, out var state))
        {
            AdoptVerifiedSecret(primarySecret);
            return state;
        }

        if (hasBackupSecret &&
            (!hasPrimarySecret || !SecretsEqual(primarySecret, backupSecret)) &&
            TryLoadWithSecret(_file, backupSecret, out state))
        {
            AdoptVerifiedSecret(backupSecret);
            return state;
        }

        // Only fall back to state.v2.bak after both secrets have been tried against the
        // primary state. This prevents a stale but readable key from forcing a rollback.
        if (hasPrimarySecret && TryLoadWithSecret(_backup, primarySecret, out state))
        {
            AdoptVerifiedSecret(primarySecret);
            state.IntegrityIssueDetected = true;
            AddAudit(state, "Integrity", "File dữ liệu chính không hợp lệ; đã khôi phục từ bản sao lưu.");
            return state;
        }

        if (hasBackupSecret &&
            (!hasPrimarySecret || !SecretsEqual(primarySecret, backupSecret)) &&
            TryLoadWithSecret(_backup, backupSecret, out state))
        {
            AdoptVerifiedSecret(backupSecret);
            state.IntegrityIssueDetected = true;
            AddAudit(state, "Integrity", "File dữ liệu chính không hợp lệ; đã khôi phục từ bản sao lưu.");
            return state;
        }

        if (HasPersistedState())
        {
            throw new InvalidDataException(
                $"Dữ liệu FocusLock tồn tại nhưng không xác minh được tại {_directory}. " +
                "File và cả hai secret được giữ nguyên; FocusLock sẽ không tạo trạng thái mặc định ghi đè dữ liệu cũ.");
        }

        return new AppState();
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(_directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, _json);
        var envelope = new Envelope
        {
            Payload = Convert.ToBase64String(payload),
            Hmac = ComputeHmac(payload, _secret)
        };
        var serialized = JsonSerializer.Serialize(envelope, _json);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(_file) && TryLoad(_file, out _)) File.Copy(_file, _backup, overwrite: true);
        File.Move(temp, _file, overwrite: true);
    }

    /// <summary>
    /// Creates a complete portable FocusLock backup. The archive intentionally contains
    /// guard.secret because the persisted state is HMAC-signed with that secret. Treat the
    /// resulting .focuslockbackup file as private data.
    /// </summary>
    public string CreatePortableBackup(string destinationPath, AppState state)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new InvalidOperationException("Chưa chọn nơi lưu bản sao lưu.");

        var fullPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Đường dẫn sao lưu không hợp lệ.");

        Directory.CreateDirectory(parent);
        Save(state);

        var stateBytes = File.ReadAllBytes(_file);
        var secretBytes = File.ReadAllBytes(_secretFile);
        if (secretBytes.Length < 32)
            throw new InvalidOperationException("guard.secret hiện tại không hợp lệ; không thể tạo backup an toàn.");

        var manifest = new BackupManifest
        {
            SchemaVersion = state.SchemaVersion,
            StateSha256 = Convert.ToHexString(SHA256.HashData(stateBytes)),
            SecretSha256 = Convert.ToHexString(SHA256.HashData(secretBytes))
        };

        var temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
                WriteEntry(zip, "state.v2.json", stateBytes);
                WriteEntry(zip, "guard.secret", secretBytes);
            }
            File.Move(temp, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Validates the entire archive before touching live data. A safety backup of the
    /// current state is created automatically in Data\Backups before replacement.
    /// </summary>
    public AppState RestorePortableBackup(string sourcePath, AppState currentState, int maxSupportedSchema, out string safetyBackupPath)
    {
        safetyBackupPath = "";
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("Chưa chọn file backup để khôi phục.");

        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Không tìm thấy file backup.", fullPath);

        byte[] stateBytes;
        byte[] secretBytes;
        BackupManifest manifest;

        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false))
        {
            var manifestBytes = ReadEntry(zip, "manifest.json", 128 * 1024);
            stateBytes = ReadEntry(zip, "state.v2.json", 64 * 1024 * 1024);
            secretBytes = ReadEntry(zip, "guard.secret", 4096);

            manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes, _json)
                       ?? throw new InvalidOperationException("manifest.json của backup không hợp lệ.");
        }

        if (!string.Equals(manifest.Product, "FocusLock", StringComparison.Ordinal) || manifest.FormatVersion != 1)
            throw new InvalidOperationException("Đây không phải định dạng FocusLock Backup được hỗ trợ.");
        if (secretBytes.Length < 32)
            throw new InvalidOperationException("Backup có guard.secret không hợp lệ.");
        if (!FixedHexEquals(manifest.StateSha256, SHA256.HashData(stateBytes)) ||
            !FixedHexEquals(manifest.SecretSha256, SHA256.HashData(secretBytes)))
            throw new InvalidOperationException("Backup bị hỏng hoặc đã bị thay đổi.");
        if (!TryParseEnvelope(stateBytes, secretBytes, out var restored))
            throw new InvalidOperationException("Không xác minh được chữ ký dữ liệu trong backup.");
        if (restored.SchemaVersion > maxSupportedSchema)
            throw new InvalidOperationException($"Backup dùng schema {restored.SchemaVersion}, mới hơn bản FocusLock hiện tại ({maxSupportedSchema}). Hãy cập nhật FocusLock trước khi Restore.");
        if (manifest.SchemaVersion != restored.SchemaVersion)
            throw new InvalidOperationException("Schema trong manifest không khớp dữ liệu backup.");

        // Before any destructive write, create a complete rollback point with the current key.
        var safetyDir = Path.Combine(_directory, "Backups");
        Directory.CreateDirectory(safetyDir);
        safetyBackupPath = Path.Combine(safetyDir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmssfff}.focuslockbackup");
        CreatePortableBackup(safetyBackupPath, currentState);

        var oldStateBytes = File.Exists(_file) ? File.ReadAllBytes(_file) : Array.Empty<byte>();
        var oldBackupBytes = File.Exists(_backup) ? File.ReadAllBytes(_backup) : Array.Empty<byte>();
        var oldSecretBytes = File.Exists(_secretFile) ? File.ReadAllBytes(_secretFile) : _secret.ToArray();
        var oldSecretBackupBytes = TryReadSecretBytes(_secretBackupFile, out var oldSecretBackup)
            ? oldSecretBackup
            : Array.Empty<byte>();
        var oldSecretMemory = _secret.ToArray();

        var secretTemp = _secretFile + ".restore-" + Guid.NewGuid().ToString("N");
        var stateTemp = _file + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(secretTemp, secretBytes);
            File.WriteAllBytes(stateTemp, stateBytes);

            try { if (File.Exists(_secretFile)) File.SetAttributes(_secretFile, FileAttributes.Normal); } catch { }
            File.Move(secretTemp, _secretFile, overwrite: true);
            _secret = secretBytes.ToArray();
            try { File.SetAttributes(_secretFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
            WriteSecretBackup(_secret);

            File.Move(stateTemp, _file, overwrite: true);
            File.Copy(_file, _backup, overwrite: true);
            return restored;
        }
        catch
        {
            _secret = oldSecretMemory;
            TryRestoreBytes(_secretFile, oldSecretBytes);
            TryRestoreBytes(_secretBackupFile, oldSecretBackupBytes);
            try { if (File.Exists(_secretFile)) File.SetAttributes(_secretFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
            try { if (File.Exists(_secretBackupFile)) File.SetAttributes(_secretBackupFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
            TryRestoreBytes(_file, oldStateBytes);
            TryRestoreBytes(_backup, oldBackupBytes);
            throw;
        }
        finally
        {
            try { if (File.Exists(secretTemp)) File.Delete(secretTemp); } catch { }
            try { if (File.Exists(stateTemp)) File.Delete(stateTemp); } catch { }
        }
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
        // Persistence must stay inside the user's code folder and must not depend on
        // environment variables, the Windows working directory, or a versioned OneDir.
        // Preferred layout:
        //   <CodeRoot>\FocusLock.sln
        //   <CodeRoot>\FocusLock-OneDir-*\Service\FocusLock.Service.exe
        //   <CodeRoot>\FocusLock-Data\Data\state.v2.json
        var baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var serviceDir = new DirectoryInfo(baseDir);

        // In the developer/code-folder edition, walk upward and anchor persistence at
        // the directory that contains FocusLock.sln. This is deterministic after reboot.
        DirectoryInfo? cursor = serviceDir;
        for (var depth = 0; cursor is not null && depth < 6; depth++, cursor = cursor.Parent)
        {
            try
            {
                if (File.Exists(Path.Combine(cursor.FullName, "FocusLock.sln")))
                    return Path.Combine(cursor.FullName, "FocusLock-Data");
            }
            catch { }
        }

        // Fallback for a versioned OneDir located directly inside the code folder.
        if (serviceDir.Name.Equals("Service", StringComparison.OrdinalIgnoreCase) && serviceDir.Parent is not null)
        {
            var runtimeRoot = serviceDir.Parent;
            if ((runtimeRoot.Name.StartsWith("FocusLock-OneDir", StringComparison.OrdinalIgnoreCase) ||
                 runtimeRoot.Name.Equals("publish", StringComparison.OrdinalIgnoreCase)) &&
                runtimeRoot.Parent is not null)
            {
                return Path.Combine(runtimeRoot.Parent.FullName, "FocusLock-Data");
            }

            return Path.Combine(runtimeRoot.FullName, "FocusLock-Data");
        }

        // Development/debug fallback when no solution marker can be found.
        return Path.Combine(serviceDir.FullName, "FocusLock-Data");
    }

    private bool TryLoad(string path, out AppState state)
    {
        return TryLoadWithSecret(path, _secret, out state);
    }

    private bool TryLoadWithSecret(string path, byte[] secret, out AppState state)
    {
        state = new AppState();
        try
        {
            if (secret.Length < 32 || !File.Exists(path)) return false;
            return TryParseEnvelope(File.ReadAllBytes(path), secret, out state);
        }
        catch { return false; }
    }

    private bool TryParseEnvelope(byte[] envelopeBytes, byte[] secret, out AppState state)
    {
        state = new AppState();
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(StripUtf8Bom(envelopeBytes), _json);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Hmac)) return false;
            var payload = Convert.FromBase64String(envelope.Payload);
            var actual = Convert.FromHexString(envelope.Hmac);
            var expected = Convert.FromHexString(ComputeHmac(payload, secret));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
            state = JsonSerializer.Deserialize<AppState>(payload, _json) ?? new AppState();
            return true;
        }
        catch { return false; }
    }

    private byte[] LoadOrCreateSecret()
    {
        var hasPrimary = TryReadSecretBytes(_secretFile, out var primary);
        var hasBackup = TryReadSecretBytes(_secretBackupFile, out var backupSecret);

        // Critical rule: when signed state already exists, NEVER copy one secret over the
        // other before HMAC verification. The previous fail-safe still trusted any readable
        // primary secret and immediately overwrote guard.secret.bak with it. If primary was
        // stale but backup matched the state, that destroyed the only usable recovery key.
        if (HasPersistedState())
        {
            if (hasPrimary) return primary;
            if (hasBackup) return backupSecret;

            throw new InvalidDataException(
                $"FocusLock có dữ liệu cũ tại {_directory} nhưng cả guard.secret và guard.secret.bak đều không đọc được/hợp lệ. " +
                "Dữ liệu được giữ nguyên; không tạo secret mới.");
        }

        // No signed state exists yet, so normal key bootstrap/recovery is safe.
        if (hasPrimary)
        {
            if (!hasBackup) WriteSecretBackup(primary);
            return primary;
        }

        if (hasBackup)
        {
            WriteSecretFile(_secretFile, backupSecret);
            return backupSecret;
        }

        if (HasAnyPersistenceArtifact())
        {
            throw new InvalidDataException(
                $"FocusLock có persistence artifact tại {_directory} nhưng không có secret hợp lệ. " +
                "Dữ liệu được giữ nguyên; không tạo secret mới.");
        }

        var secret = RandomNumberGenerator.GetBytes(64);
        WriteSecretFile(_secretFile, secret);
        WriteSecretBackup(secret);
        return secret;
    }

    private void AdoptVerifiedSecret(byte[] verifiedSecret)
    {
        _secret = verifiedSecret;

        // Repair primary only AFTER a state file has proved which key is correct.
        if (!TryReadSecretBytes(_secretFile, out var currentPrimary) ||
            !SecretsEqual(currentPrimary, verifiedSecret))
        {
            WriteSecretFile(_secretFile, verifiedSecret);
        }

        // Do not overwrite a different readable backup key here. It may still be the key
        // for state.v2.bak and is useful for recovery. Only create the backup if missing.
        if (!TryReadSecretBytes(_secretBackupFile, out _))
            WriteSecretBackup(verifiedSecret);
    }

    private static bool SecretsEqual(byte[] a, byte[] b)
    {
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private bool HasPersistedState()
    {
        try { return File.Exists(_file) || File.Exists(_backup); }
        catch { return true; }
    }

    private bool HasAnyPersistenceArtifact()
    {
        try
        {
            if (!Directory.Exists(_directory)) return false;
            var names = Directory.EnumerateFiles(_directory)
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names.Contains(Path.GetFileName(_file)) ||
                   names.Contains(Path.GetFileName(_backup)) ||
                   names.Contains(Path.GetFileName(_secretFile)) ||
                   names.Contains(Path.GetFileName(_secretBackupFile));
        }
        catch
        {
            // If the directory itself cannot be inspected, generating a new key is
            // unsafe. Treat it as containing persistence data and fail closed.
            return true;
        }
    }

    private static bool TryReadSecretBytes(string path, out byte[] secret)
    {
        secret = Array.Empty<byte>();
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 32) return false;
            secret = bytes;
            return true;
        }
        catch { return false; }
    }

    private void WriteSecretBackup(byte[] secret)
    {
        if (TryReadSecretBytes(_secretBackupFile, out var current) &&
            current.Length == secret.Length &&
            CryptographicOperations.FixedTimeEquals(current, secret))
            return;

        WriteSecretFile(_secretBackupFile, secret);
    }

    private static void WriteSecretFile(string path, byte[] secret)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, secret);
            try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Move(temp, path, overwrite: true);
            try { File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System); } catch { }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> bytes)
    {
        // System.Text.Json / Utf8JsonReader expects JSON text bytes, not a UTF-8 BOM.
        // Older FocusLock builds wrote the outer envelope with Encoding.UTF8, whose
        // preamble is EF BB BF. HMAC covers the decoded Payload bytes, so removing
        // the BOM from the outer JSON envelope does not alter or weaken integrity.
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }

    private static string ComputeHmac(byte[] payload, byte[] secret)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(payload));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int maxBytes)
    {
        var matches = zip.Entries.Where(x => string.Equals(x.FullName, name, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Backup thiếu hoặc trùng file bắt buộc: {name}.");
        var entry = matches[0];
        if (entry.Length < 0 || entry.Length > maxBytes)
            throw new InvalidOperationException($"File {name} trong backup vượt giới hạn an toàn.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        long total = 0;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"File {name} trong backup vượt giới hạn an toàn.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool FixedHexEquals(string? expectedHex, byte[] actualBytes)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        try
        {
            var expected = Convert.FromHexString(expectedHex);
            return expected.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, actualBytes);
        }
        catch { return false; }
    }

    private static void TryRestoreBytes(string path, byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, bytes);
            }
        }
        catch { }
    }

    private static void AddAudit(AppState state, string type, string message)
    {
        state.AuditLog.Insert(0, new AuditEvent { Type = type, Message = message });
        if (state.AuditLog.Count > 500) state.AuditLog.RemoveRange(500, state.AuditLog.Count - 500);
    }
}
