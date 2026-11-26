using GameClub.Server.Setup;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server;

public partial class Program
{
    public static Task Main(string[] args)
    {
        if (SetupCli.TryHandle(args, out var cliExit))
        {
            Environment.Exit(cliExit);
        }

        return ServerApplication.RunAsync(args);
    }

    internal static async Task ApplyAuditImmutabilityAsync(Data.AppDbContext db)
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