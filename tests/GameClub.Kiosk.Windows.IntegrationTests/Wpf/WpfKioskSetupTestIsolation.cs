#if NET48
using System;
using System.IO;
using System.Text.Json;
using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

internal sealed class WpfKioskSetupTestIsolation : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _tempDir;
    private readonly string _priorSetupJson;
    private readonly string _priorCheckpointUrl;
    private readonly string _priorCheckpointAdmin;
    private readonly bool _hadPriorSetupJson;
    private readonly bool _hadPriorCheckpointUrl;
    private readonly bool _hadPriorCheckpointAdmin;

    public static WpfKioskSetupTestIsolation CreateWithCompleteSetup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gcm-wpf-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var setupPath = Path.Combine(tempDir, "setup.json");
        var document = new KioskSetupDocument
        {
            ServerBase = "http://127.0.0.1:5225",
            KioskManagerHash = "wpf-smoke-mgr-hash",
            KioskOperatorHash = "wpf-smoke-op-hash",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(setupPath, JsonSerializer.Serialize(document, JsonOptions));
        return new WpfKioskSetupTestIsolation(tempDir, setupPath);
    }

    public static WpfKioskSetupTestIsolation CreateWithoutSetup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gcm-wpf-first-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var setupPath = Path.Combine(tempDir, "setup.json");
        return new WpfKioskSetupTestIsolation(tempDir, setupPath);
    }

    private WpfKioskSetupTestIsolation(string tempDir, string setupJsonPath)
    {
        _tempDir = tempDir;
        _priorSetupJson = Environment.GetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar) ?? string.Empty;
        _hadPriorSetupJson = Environment.GetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar) != null;
        _priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? string.Empty;
        _hadPriorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") != null;
        _priorCheckpointAdmin = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD") ?? string.Empty;
        _hadPriorCheckpointAdmin = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD") != null;
        Environment.SetEnvironmentVariable(KioskSetupPaths.SetupJsonPathEnvVar, setupJsonPath);
        Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", null);
        Environment.SetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            KioskSetupPaths.SetupJsonPathEnvVar,
            _hadPriorSetupJson ? _priorSetupJson : null);
        Environment.SetEnvironmentVariable(
            "CHECKPOINT_SERVER_URL",
            _hadPriorCheckpointUrl ? _priorCheckpointUrl : null);
        Environment.SetEnvironmentVariable(
            "CHECKPOINT_ADMIN_PASSWORD",
            _hadPriorCheckpointAdmin ? _priorCheckpointAdmin : null);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
#endif