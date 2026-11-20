using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameClub.Server.Setup;

public static class SetupCli
{
    private static readonly PasswordHasher<OperatorUserEntity> PinHasher = new();

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        return args[0] switch
        {
            "--hash-pin" => RunHashPin(out exitCode),
            "--check-default-pins" => RunCheckDefaultPins(args, out exitCode),
            "--set-pin" => RunSetPin(args, out exitCode),
            _ => false,
        };
    }

    private static bool RunHashPin(out int exitCode)
    {
        exitCode = 0;
        var pin = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(pin))
        {
            exitCode = 2;
            return true;
        }

        var user = new OperatorUserEntity { Id = Guid.Empty, Username = "cli" };
        Console.Out.Write(OperatorAuthService.HashPin(user, pin));
        return true;
    }

    private static bool RunCheckDefaultPins(string[] args, out int exitCode)
    {
        exitCode = 0;
        return RunWithDb(async (db, ct) =>
        {
            var users = await db.OperatorUsers.AsNoTracking().ToListAsync(ct);
            foreach (var user in users)
            {
                if (VerifyPin(user, "1234") || VerifyPin(user, "1111"))
                {
                    Console.Out.WriteLine(user.Username);
                }
            }
        }, out exitCode);
    }

    private static bool RunSetPin(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2)
        {
            exitCode = 2;
            return true;
        }

        var username = args[1];
        var pin = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(pin))
        {
            exitCode = 2;
            return true;
        }

        return RunWithDb(async (db, ct) =>
        {
            var user = await db.OperatorUsers.FirstOrDefaultAsync(u => u.Username == username, ct)
                ?? throw new InvalidOperationException($"User '{username}' not found.");
            user.PinHash = OperatorAuthService.HashPin(user, pin);
            await db.SaveChangesAsync(ct);
        }, out exitCode);
    }

    private static bool RunWithDb(Func<AppDbContext, CancellationToken, Task> action, out int exitCode)
    {
        exitCode = 0;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile(@"C:\ProgramData\Checkpoint\config\server.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var connectionString = configuration.GetConnectionString("Default")
                ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
                ?? "Host=localhost;Database=gameclub;Username=postgres;Password=postgres";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
            using var db = new AppDbContext(options);
            action(db, default).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }

        return true;
    }

    private static bool VerifyPin(OperatorUserEntity user, string pin)
    {
        var result = PinHasher.VerifyHashedPassword(user, user.PinHash, pin);
        return result is not PasswordVerificationResult.Failed;
    }
}