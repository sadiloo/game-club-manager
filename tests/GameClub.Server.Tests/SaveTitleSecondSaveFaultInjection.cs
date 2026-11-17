using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>
/// Counts SaveChanges while active; throws on the second call so partial commits
/// without an explicit DB transaction leave mutated rows visible to other connections.
/// </summary>
public sealed class SaveTitleSecondSaveFaultInterceptor(GameClubWebApplicationFactory hostFactory)
    : SaveChangesInterceptor
{
    public static SaveTitleSaveFaultGate Activate(GameClubWebApplicationFactory factory, int gameTitleId, bool throwOnSecondSave)
    {
        var gate = new SaveTitleSaveFaultGate(factory, gameTitleId, throwOnSecondSave, factory.ConnectionString);
        factory.SaveTitleSaveFaultGate = gate;
        return gate;
    }

    public static void Deactivate(GameClubWebApplicationFactory factory, SaveTitleSaveFaultGate gate)
    {
        if (ReferenceEquals(factory.SaveTitleSaveFaultGate, gate))
        {
            factory.SaveTitleSaveFaultGate = null;
        }
    }

    private SaveTitleSaveFaultGate? Gate => hostFactory.SaveTitleSaveFaultGate;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate;
        if (gate is null || !gate.ThrowOnSecondSave)
        {
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        gate.SaveAttempts++;
        if (gate.SaveAttempts == 2)
        {
            throw new InvalidOperationException("test_injected_second_save_changes_failure");
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate;
        if (gate is not null && gate.SaveAttempts == 1)
        {
            gate.TitleNameAfterFirstSaveOtherConnection = await ReadTitleNameAsync(gate.ConnectionString, gate.GameTitleId);
            gate.RateHourlyP1AfterFirstSaveOtherConnection = await ReadRateHourlyAsync(gate.ConnectionString, gate.GameTitleId, 1);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private static async Task<string?> ReadTitleNameAsync(string connectionString, int gameTitleId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "Name" FROM game_titles WHERE "Id" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", gameTitleId);
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar as string;
    }

    private static async Task<long?> ReadRateHourlyAsync(string connectionString, int gameTitleId, int playerCount)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "HourlyToman" FROM game_rate_lines
            WHERE "GameTitleId" = @id AND "PlayerCount" = @pc
            """,
            conn);
        cmd.Parameters.AddWithValue("id", gameTitleId);
        cmd.Parameters.AddWithValue("pc", playerCount);
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar is long l ? l : scalar is int i ? i : null;
    }
}

public sealed class SaveTitleSaveFaultGate(
    GameClubWebApplicationFactory factory,
    int gameTitleId,
    bool throwOnSecondSave,
    string connectionString) : IDisposable
{
    public int GameTitleId { get; } = gameTitleId;

    public bool ThrowOnSecondSave { get; } = throwOnSecondSave;

    public string ConnectionString { get; } = connectionString;

    public int SaveAttempts { get; internal set; }

    public string? TitleNameAfterFirstSaveOtherConnection { get; internal set; }

    public long? RateHourlyP1AfterFirstSaveOtherConnection { get; internal set; }

    public void Dispose() => SaveTitleSecondSaveFaultInterceptor.Deactivate(factory, this);
}

internal sealed class SaveTitleSecondSaveFaultDbContextOptions(SaveTitleSecondSaveFaultInterceptor interceptor)
    : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.AddInterceptors(interceptor);
}

internal static class SaveTitleSecondSaveFaultInjectionRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<SaveTitleSecondSaveFaultInterceptor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<AppDbContext>, SaveTitleSecondSaveFaultDbContextOptions>());
    }
}