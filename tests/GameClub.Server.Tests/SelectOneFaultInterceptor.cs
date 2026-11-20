using System.Data.Common;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameClub.Server.Tests;

/// <summary>Makes <c>SELECT 1</c> fail so server-status db probes return dbOk=false without a broken connection string.</summary>
internal sealed class SelectOneFaultDbContextOptions : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
    {
        var interceptor = serviceProvider.GetRequiredService<SelectOneFaultInterceptor>();
        optionsBuilder.AddInterceptors(interceptor);
    }
}

internal sealed class SelectOneFaultInterceptor : DbCommandInterceptor
{
    public static bool FaultEnabled { get; set; }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        if (ShouldFault(command))
        {
            throw new TimeoutException("simulated database probe failure");
        }

        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFault(command))
        {
            throw new TimeoutException("simulated database probe failure");
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        if (ShouldFault(command))
        {
            throw new TimeoutException("simulated database probe failure");
        }

        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFault(command))
        {
            throw new TimeoutException("simulated database probe failure");
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static bool ShouldFault(DbCommand command)
        => FaultEnabled && IsSelectOneProbe(command);

    private static bool IsSelectOneProbe(DbCommand command)
        => string.Equals(command.CommandText?.Trim(), "SELECT 1", StringComparison.OrdinalIgnoreCase);
}