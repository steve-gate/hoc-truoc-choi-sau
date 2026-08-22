namespace FocusLock.Service.Services;

public sealed class GuardWorker : BackgroundService
{
    private readonly FocusAuthorityEngine _engine;
    private readonly ILogger<GuardWorker> _logger;

    public GuardWorker(FocusAuthorityEngine engine, ILogger<GuardWorker> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSave = DateTime.UtcNow;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _engine.GuardTick();
                    _engine.EnforceEntertainmentPolicies();

                    if ((DateTime.UtcNow - lastSave).TotalSeconds >= 10)
                    {
                        _engine.Save();
                        lastSave = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Guard tick failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Never intentionally leave a V7 "Khóa tại chỗ" process suspended when
            // the service is being stopped for an update/shutdown.
            try { _engine.ReleaseEntertainmentLock(); } catch { }
            _engine.Save();
        }
    }
}
