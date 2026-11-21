using GameClub.Kiosk.Core.Admin;
using Microsoft.AspNetCore.Identity;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskCredentialVerifierTests
{
    [Fact]
    public void verify_matches_server_style_hash()
    {
        var row = new KioskCredentialHashRow { Role = "manager" };
        var hash = new PasswordHasher<KioskCredentialHashRow>().HashPassword(row, "Secret1");
        Assert.True(KioskCredentialVerifier.Verify(hash, "Secret1", "manager"));
        Assert.False(KioskCredentialVerifier.Verify(hash, "wrong", "manager"));
    }
}