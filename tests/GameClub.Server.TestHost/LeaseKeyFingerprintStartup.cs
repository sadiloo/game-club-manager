using GameClub.Server.Kiosk;
using GameClub.Server.Testing;
using Microsoft.Extensions.Hosting;

namespace GameClub.Server.TestHost;

internal sealed class LeaseKeyFingerprintStartup(IKioskLeaseSigner signer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(KioskLeaseSigningKeyDiagnostics.FormatFingerprintLine(signer.PublicKeyPem));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}