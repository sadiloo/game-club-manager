using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Tests;

[CollectionDefinition("kiosk-attach-seam", DisableParallelization = true)]
public sealed class KioskAttachPathSeamCollection : ICollectionFixture<KioskAttachPathSeamWebApplicationFactory>;

[Collection("kiosk-attach-seam")]
public sealed class KioskAttachPathSeamTests(KioskAttachPathSeamWebApplicationFactory factory)
{
    [Fact]
    public async Task active_session_unique_violation_on_member_start_resolves_via_attach_path()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-ATTACH-UNIT";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var kioskSessions = scope.ServiceProvider.GetRequiredService<KioskSessionService>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "AttachUnit",
            Mobile = "09129998877",
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customerId, "attachunit", "pass", default);

        var raceWinnerSessionId = Guid.Empty;
        var seamFired = 0;
        try
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = async _ =>
            {
                Interlocked.Exchange(ref seamFired, 1);
                raceWinnerSessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), stationId);
                return PostgresTestFaults.ActiveSessionUniqueViolation();
            };
            var session = await kioskSessions.AttachOrStartMemberAsync(customerId, stationId, default);
            Assert.Equal(1, seamFired);
            Assert.Equal(raceWinnerSessionId, session.Id);
            Assert.Equal(customerId, (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == raceWinnerSessionId)).CustomerId);
        }
        finally
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = null;
        }
    }

    [Fact]
    public async Task serialization_failure_on_member_start_is_not_swallowed_by_attach_catch()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-ATTACH-SER";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var kioskSessions = scope.ServiceProvider.GetRequiredService<KioskSessionService>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Ser",
            Mobile = "09128887766",
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customerId, "serfail", "pass", default);

        var seamFired = 0;
        try
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = _ =>
            {
                Interlocked.Exchange(ref seamFired, 1);
                return Task.FromResult(PostgresTestFaults.SerializationFailure());
            };
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
                kioskSessions.AttachOrStartMemberAsync(customerId, stationId, default));
            Assert.Equal(1, seamFired);
            Assert.Null(PostgresTestFaults.FindPostgres(ex, PostgresErrorCodes.UniqueViolation));
        }
        finally
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = null;
        }
    }

    [Fact]
    public async Task unrelated_unique_violation_on_member_start_is_not_swallowed_by_attach_catch()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-ATTACH-UX";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var kioskSessions = scope.ServiceProvider.GetRequiredService<KioskSessionService>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Ux",
            Mobile = "09127776655",
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customerId, "uxfail", "pass", default);

        var seamFired = 0;
        try
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = _ =>
            {
                Interlocked.Exchange(ref seamFired, 1);
                return Task.FromResult(PostgresTestFaults.UniqueOnConstraint(PostgresErrors.OpenPauseIndexName));
            };
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
                kioskSessions.AttachOrStartMemberAsync(customerId, stationId, default));
            Assert.Equal(1, seamFired);
            var pg = PostgresTestFaults.FindPostgres(ex, PostgresErrorCodes.UniqueViolation);
            Assert.NotNull(pg);
            Assert.Equal(PostgresErrors.OpenPauseIndexName, pg!.ConstraintName);
            Assert.NotEqual(PostgresErrors.ActiveSessionIndexName, pg.ConstraintName);
        }
        finally
        {
            KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = null;
        }
    }
}