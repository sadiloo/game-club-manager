using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskSetupCoreTests
{
    [Theory]
    [InlineData("192.168.1.10", "http://192.168.1.10:5000")]
    [InlineData("http://192.168.1.10:5000/", "http://192.168.1.10:5000")]
    [InlineData("http://host:8080", "http://host:8080")]
    [InlineData("۱۹۲.۱۶۸.۰.۱۰", "http://192.168.0.10:5000")]
    public void normalizer_adds_scheme_and_default_port(string input, string expected)
    {
        Assert.True(KioskServerUrlNormalizer.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void normalizer_rejects_empty_and_invalid()
    {
        Assert.False(KioskServerUrlNormalizer.TryNormalize("", out _, out var code));
        Assert.Equal("empty", code);
        Assert.False(KioskServerUrlNormalizer.TryNormalize("ftp://x", out _, out _));
    }

    [Fact]
    public void setup_json_path_env_override_points_at_isolated_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gcm-setup-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var isolated = Path.Combine(dir, "setup.json");
        var prior = Environment.GetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar, isolated);
            Assert.Equal(isolated, KioskSetupPaths.SetupJsonPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar, prior);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void setup_store_roundtrip_and_gate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gcm-setup-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "setup.json");
        var store = new KioskSetupStore(path);
        Assert.True(store.ShouldShowFirstRun());

        store.Save(new KioskSetupDocument
        {
            ServerBase = "http://127.0.0.1:5000",
            KioskManagerHash = "m",
            KioskOperatorHash = "o",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsComplete);
        Assert.False(store.ShouldShowFirstRun());
    }
}