using GameClub.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameClub.Server.Tests;

/// <summary>
/// Test host replacement for <see cref="DeadPcSweepBackgroundService"/> so integration
/// tests can disable periodic sweeps and avoid row-lock races with checkpoint APIs.
/// </summary>
internal sealed class TestDeadPcSweepBackgroundService(
    GameClubWebApplicationFactory factory,
    IServiceScopeFactory scopeFactory,
    IOptions<DeadPcSweepOptions> options,
    ILogger<TestDeadPcSweepBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!factory.DeadPcBackgroundSweepEnabled)
            {
                continue;
            }

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
