using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FocusLock.Shared.Protocol;

namespace FocusLock.NativeHost;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string AllowedOrigin = $"chrome-extension://{PipeNames.BrowserExtensionId}/";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(origin, AllowedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                await WriteNativeAsync(Console.OpenStandardOutput(), new
                {
                    type = "error",
                    ok = false,
                    message = "FocusLock Native Host: extension origin không được phép."
                });
                return 2;
            }

            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();

            while (true)
            {
                using var doc = await ReadNativeAsync(input);
                if (doc is null) break;

                var root = doc.RootElement;
                var type = GetString(root, "type");
                if (!string.Equals(type, "context", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteNativeAsync(output, new { type = "error", ok = false, message = "Message type không hỗ trợ." });
                    continue;
                }

                var sample = new BrowserContextSample
                {
                    Browser = GetString(root, "browser"),
                    Url = GetString(root, "url"),
                    Title = GetString(root, "title"),
                    Host = GetString(root, "host"),
                    WindowFocused = GetBool(root, "windowFocused"),
                    ExtensionVersion = GetString(root, "extensionVersion"),
                    ObservedUtc = DateTime.UtcNow
                };

                var response = await SendToGuardAsync(new PipeRequest
                {
                    Command = "browserContext",
                    BrowserContext = sample
                });

                var d = response?.BrowserDecision;
                await WriteNativeAsync(output, new
                {
                    type = "decision",
                    ok = response?.Ok == true,
                    bridgeOnline = d?.BridgeOnline == true,
                    matched = d?.Matched == true,
                    blocked = d?.Blocked == true,
                    category = d?.Category ?? "Neutral",
                    ruleId = d?.RuleId ?? "",
                    ruleName = d?.RuleName ?? "",
                    host = d?.Host ?? sample.Host,
                    url = d?.Url ?? sample.Url,
                    message = response?.Message ?? "FocusLock Guard chưa phản hồi.",
                    entertainmentBalanceSeconds = d?.EntertainmentBalanceSeconds ?? 0,
                    focusProgressSeconds = d?.FocusProgressSeconds ?? 0
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine(ex); } catch { }
            return 1;
        }
    }

    private static async Task<PipeResponse?> SendToGuardAsync(PipeRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeNames.Guard, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json));
            var line = await reader.ReadLineAsync(cts.Token);
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<PipeResponse>(line, Json);
        }
        catch (Exception ex)
        {
            return new PipeResponse { Ok = false, Message = "Không kết nối được FocusLock Guard: " + ex.Message };
        }
    }

    private static async Task<JsonDocument?> ReadNativeAsync(Stream input)
    {
        var lengthBytes = new byte[4];
        var first = await input.ReadAsync(lengthBytes.AsMemory(0, 4));
        if (first == 0) return null;
        var read = first;
        while (read < 4)
        {
            var n = await input.ReadAsync(lengthBytes.AsMemory(read, 4 - read));
            if (n == 0) return null;
            read += n;
        }

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Native message quá lớn hoặc không hợp lệ.");
        var payload = new byte[length];
        await input.ReadExactlyAsync(payload);
        return JsonDocument.Parse(payload);
    }

    private static async Task WriteNativeAsync(Stream output, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var length = BitConverter.GetBytes(bytes.Length);
        await output.WriteAsync(length);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
