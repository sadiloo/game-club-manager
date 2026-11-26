using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskTrustFileClientFactoryTests
{
    [Fact]
    public void production_windows_canonical_path_selects_pipe_client_without_legacy_dev()
    {
        var priorUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var priorAdmin = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD");
        var priorDirect = Environment.GetEnvironmentVariable(KioskTrustFileClientFactory.DirectWriteEnvVar);
        var priorPipe = Environment.GetEnvironmentVariable(KioskTrustFileClientFactory.UseGuardPipeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", null);
            Environment.SetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD", null);
            Environment.SetEnvironmentVariable(KioskTrustFileClientFactory.DirectWriteEnvVar, null);
            Environment.SetEnvironmentVariable(KioskTrustFileClientFactory.UseGuardPipeEnvVar, null);
            var path = KioskTrustFiles.TrustFilePath(KioskTrustFiles.SetupFileName);
            if (OperatingSystem.IsWindows())
            {
                var client = KioskTrustFileClientFactory.ForPath(path);
                Assert.IsType<KioskTrustFilePipeClient>(client);
            }
            else
            {
                var client = KioskTrustFileClientFactory.ForPath(path);
                Assert.IsType<KioskTrustFileDirectClient>(client);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", priorUrl);
            Environment.SetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD", priorAdmin);
            Environment.SetEnvironmentVariable(KioskTrustFileClientFactory.DirectWriteEnvVar, priorDirect);
            Environment.SetEnvironmentVariable(KioskTrustFileClientFactory.UseGuardPipeEnvVar, priorPipe);
        }
    }
}