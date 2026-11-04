using GameClub.Server.Data;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<BranchRates>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<StationService>();
        builder.Services.AddScoped<SessionBilling>();
        builder.Services.AddScoped<SessionCloser>();
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddScoped<IDeadPcMonitor, DeadPcMonitor>();

        var connectionString = builder.Configuration.GetConnectionString("Default")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? "Host=localhost;Database=gameclub;Username=postgres;Password=postgres";

        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapControllers();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await DatabaseSeed.EnsureAsync(db);
            await ApplyAuditImmutabilityAsync(db);
        }

        await app.RunAsync();
    }

    internal static async Task ApplyAuditImmutabilityAsync(AppDbContext db)
    {
        const string sql = """
            CREATE OR REPLACE FUNCTION audit_log_immutable()
            RETURNS trigger AS $$
            BEGIN
              RAISE EXCEPTION 'audit_log is append-only';
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS audit_log_no_update ON audit_log;
            CREATE TRIGGER audit_log_no_update
              BEFORE UPDATE OR DELETE ON audit_log
              FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
            """;

        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
