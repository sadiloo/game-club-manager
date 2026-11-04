namespace GameClub.Server.Services;

public sealed class DeadPcSweepOptions
{
    public const string SectionName = "DeadPc";

    public int SweepIntervalSeconds { get; set; } = 30;
}

public sealed class DeadPcSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<DeadPcSweepOptions> options,
    ILogger<DeadPcSweepBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var monitor = scope.ServiceProvider.GetRequiredService<IDeadPcMonitor>();
                var closed = await monitor.RunDueClosuresAsync(stoppingToken);
                if (closed > 0)
                {
                    logger.LogInformation("Dead PC sweep closed {Count} session(s)", closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dead PC sweep failed");
            }
        }
    }
}
