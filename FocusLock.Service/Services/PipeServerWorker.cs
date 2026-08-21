using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FocusLock.Shared.Protocol;

namespace FocusLock.Service.Services;

public sealed class PipeServerWorker : BackgroundService
{
    private readonly FocusAuthorityEngine _engine;
    private readonly ILogger<PipeServerWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public PipeServerWorker(FocusAuthorityEngine engine, ILogger<PipeServerWorker> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // V5 có ít nhất 2 client hoạt động song song: Windows Agent + Browser Native Host.
        // Chạy nhiều listener để heartbeat browser không tranh pipe với heartbeat UI.
        var listeners = Enumerable.Range(0, 4).Select(_ => ListenLoopAsync(stoppingToken)).ToArray();
        await Task.WhenAll(listeners);
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe listener error");
                await Task.Delay(250, stoppingToken);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new PipeAccessRule(authenticatedUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeNames.Guard,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            8192,
            8192,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return;

        PipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<PipeRequest>(line, _json) ?? throw new InvalidOperationException("Request rỗng.");
            response = _engine.Handle(request);
        }
        catch (Exception ex)
        {
            response = new PipeResponse { Ok = false, Message = ex.Message };
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
    }
}
