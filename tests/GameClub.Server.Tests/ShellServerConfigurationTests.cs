using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShellServerConfigurationTests
{
    [Fact]
    public async Task server_json_absent_preserves_baseline_configuration()
    {
        Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_CONFIG", null);
        await using var factory = new GameClubWebApplicationFactory();
        var client = factory.CreateClientWithKey();
        var stations = await client.GetAsync("/api/stations");
        Assert.Equal(HttpStatusCode.OK, stations.StatusCode);
    }

    [Fact]
    public async Task server_json_present_loads_urls_secrets_and_connection_string()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"server-{Guid.NewGuid():N}.json");
        var json = """
            {
              "GAMECLUB_API_KEY": "json-shell-key"
            }
            """;
        await File.WriteAllTextAsync(configPath, json);
        Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_CONFIG", configPath);
        try
        {
            await using var factory = new GameClubWebApplicationFactory();
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", "json-shell-key");
            var response = await client.GetAsync("/api/stations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_CONFIG", null);
            File.Delete(configPath);
        }
    }

    [Fact]
    public void production_rejects_default_operator_session_secret()
    {
        var cs = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? "Host=localhost;Database=gameclub_test_ci;Username=postgres;Password=postgres";
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("ConnectionStrings:Default", cs);
                builder.UseSetting("OperatorSessionSecret", "dev-operator-secret");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GAMECLUB_API_KEY"] = "k",
                    });
                });
            });
            _ = factory.Server;
        });
    }
}