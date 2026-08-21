using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using FocusLock.Shared.Protocol;

namespace FocusLock.App.Services;

public sealed class ServiceClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<PipeResponse> SendAsync(PipeRequest request, int timeoutMs = 1200, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNames.Guard, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(timeout.Token);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, true, 4096, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) return Offline("Service không trả dữ liệu.");
            return JsonSerializer.Deserialize<PipeResponse>(line, _json) ?? Offline("Phản hồi service không hợp lệ.");
        }
        catch (Exception ex)
        {
            return Offline(ex.Message);
        }
    }

    private static PipeResponse Offline(string message) => new()
    {
        Ok = false,
        Message = "Không kết nối được FocusLock Guard: " + message,
        Snapshot = new ServiceSnapshot { ServiceOnline = false, ServiceStatus = "Guard offline", HeartbeatHealthy = false }
    };
}
