using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FocusLock.Shared.Protocol;

namespace FocusLock.App.Services;

public sealed class ServiceClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<PipeResponse> SendAsync(PipeRequest request, int timeoutMs = 1400, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;

        // V6.6: Guard may still be starting during Windows logon. Retry instead of
        // declaring the whole application offline after a single short timeout.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await SendOnceAsync(request, timeoutMs, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;

                // On the first failed connection, ask Windows to start the Guard.
                // The installer grants normal users START/query rights only; they
                // are not given STOP or service-configuration rights.
                if (attempt == 0)
                    await GuardServiceStarter.TryEnsureRunningAsync(cancellationToken);

                if (attempt < 4)
                    await Task.Delay(250 + (attempt * 250), cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        return Offline(lastError?.Message ?? "Không thể kết nối Guard sau nhiều lần thử.");
    }

    private async Task<PipeResponse> SendOnceAsync(PipeRequest request, int timeoutMs, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", PipeNames.Guard, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await client.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Guard chưa sẵn sàng sau {timeoutMs} ms.");
        }

        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, true, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json));

        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Guard đã kết nối nhưng phản hồi quá chậm.");
        }

        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("Guard không trả dữ liệu.");

        return JsonSerializer.Deserialize<PipeResponse>(line, _json)
               ?? throw new IOException("Phản hồi Guard không hợp lệ.");
    }

    private static PipeResponse Offline(string message) => new()
    {
        Ok = false,
        Message = "Không kết nối được FocusLock Guard: " + message,
        Snapshot = new ServiceSnapshot
        {
            ServiceOnline = false,
            ServiceStatus = "Guard offline",
            HeartbeatHealthy = false
        }
    };
}
