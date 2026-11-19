using System.Reflection;
using GameClub.Server.Data;
using GameClub.Server.Hosting;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class ServerStatusService(AppDbContext db, IConfiguration configuration, ServerRuntimeStatus runtime)
{
    public async Task<ServerStatusDto> ReadAsync(CancellationToken ct = default)
    {
        var backupsPath = configuration["Checkpoint:BackupsPath"]
            ?? Environment.GetEnvironmentVariable("CHECKPOINT_BACKUPS_PATH")
            ?? configuration["CheckpointBackupsPath"]
            ?? @"C:\ProgramData\Checkpoint\backups";

        DateTimeOffset? lastBackupUtc = null;
        if (Directory.Exists(backupsPath))
        {
            var newest = Directory.EnumerateFiles(backupsPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                lastBackupUtc = new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
            }
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var dbOk = await ProbeDatabaseAsync(ct);
        return new ServerStatusDto(runtime.ApplicationStartedAtUtc, version, lastBackupUtc, dbOk);
    }

    private async Task<bool> ProbeDatabaseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record ServerStatusDto(DateTimeOffset? StartedAtUtc, string Version, DateTimeOffset? LastBackupUtc, bool DbOk);