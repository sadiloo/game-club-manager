using System.Diagnostics;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class SetupCliAndSeedTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task hash_pin_cli_writes_pin_hash()
    {
        var dll = GetServerDll();
        if (!File.Exists(dll))
        {
            var build = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{Path.Combine(RepoRoot(), "src/GameClub.Server/GameClub.Server.csproj")}\" -c Release",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            await build.WaitForExitAsync();
            Assert.Equal(0, build.ExitCode);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{GetServerDll()}\" --hash-pin",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync("5678");
        process.StandardInput.Close();
        var hash = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.False(string.IsNullOrEmpty(hash));
    }

    [Fact]
    public async Task default_dev_seed_includes_manager_and_operator()
    {
        await S3TestHelpers.ResetAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await db.OperatorUsers.AsNoTracking().ToListAsync();
        Assert.Contains(users, u => u.Username == "manager");
        Assert.Contains(users, u => u.Username == "operator");
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string GetServerDll()
        => Path.Combine(RepoRoot(), "src", "GameClub.Server", "bin", "Release", "net10.0", "GameClub.Server.dll");
}