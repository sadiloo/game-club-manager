using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GameClub.Server.Tests;

public sealed class GameClubWebApplicationFactory : WebApplicationFactory<Program>
{
    public MutableClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public string ConnectionString { get; private set; } = "";

    public HttpClient CreateClientWithKey()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, "dev-key");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConnectionString = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? "Host=localhost;Database=gameclub_test;Username=gameclub;Password=gameclub";

        Environment.SetEnvironmentVariable("GAMECLUB_DATABASE", ConnectionString);
        Environment.SetEnvironmentVariable("GAMECLUB_API_KEY", "dev-key");
        Environment.SetEnvironmentVariable("DeadPc__SweepIntervalSeconds", "1");

        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting($"{DeadPcSweepOptions.SectionName}:SweepIntervalSeconds", "1");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureDeleted();
        db.Database.Migrate();
        DatabaseSeed.EnsureAsync(db).GetAwaiter().GetResult();
        Program.ApplyAuditImmutabilityAsync(db).GetAwaiter().GetResult();
        return host;
    }
}

internal static class TestTimes
{
    public static DateTimeOffset T(int year, int month, int day, int hour, int minute, int second = 0)
        => GameClub.Billing.TehranTime.At(year, month, day, hour, minute, second);
}

internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient client, string url, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body),
        };
        return client.SendAsync(request);
    }
}
