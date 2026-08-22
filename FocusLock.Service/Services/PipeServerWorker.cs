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
    private static int _readyLogged;

    public PipeServerWorker(FocusAuthorityEngine engine, ILogger<PipeServerWorker> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Four listeners allow the desktop agent and Browser Native Host to use the
        // service concurrently without competing for one pipe instance.
        var listeners = Enumerable.Range(0, 4)
            .Select(_ => ListenLoopAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(listeners);
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe();

                if (Interlocked.Exchange(ref _readyLogged, 1) == 0)
                    WriteDiagnostic("PIPE_READY", "Named Pipe listener created successfully.");

                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe listener error");
                WriteDiagnostic("PIPE_ERROR", ex.ToString());

                try
                {
                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        var authenticatedUsers = new SecurityIdentifier(
            WellKnownSidType.AuthenticatedUserSid, null);
        var builtInUsers = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid, null);
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null);

        // A desktop client connecting to a pipe owned by LocalSystem needs
        // Synchronize in addition to Read/Write on some Windows configurations.
        var clientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

        security.AddAccessRule(new PipeAccessRule(
            authenticatedUsers, clientRights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            builtInUsers, clientRights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            admins, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeNames.Guard,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            8192,
            8192,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(
            pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);

        using var writer = new StreamWriter(
            pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
            return;

        PipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<PipeRequest>(line, _json)
                          ?? throw new InvalidOperationException("Request rong.");
            response = _engine.Handle(request);
        }
        catch (Exception ex)
        {
            response = new PipeResponse
            {
                Ok = false,
                Message = ex.Message
            };
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
    }

    private static void WriteDiagnostic(string kind, string message)
    {
        try
        {
            var baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var serviceDir = new DirectoryInfo(baseDir);
            var focusRoot = serviceDir.Name.Equals(
                    "Service", StringComparison.OrdinalIgnoreCase)
                && serviceDir.Parent is not null
                    ? serviceDir.Parent.FullName
                    : baseDir;

            var logDir = Path.Combine(focusRoot, "Logs");
            Directory.CreateDirectory(logDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{kind}] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "service-pipe.log"), line, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never bring down the Guard.
        }
    }
}
