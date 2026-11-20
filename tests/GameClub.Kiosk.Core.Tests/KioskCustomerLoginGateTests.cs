using GameClub.Kiosk.Core.Login;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskCustomerLoginGateTests
{
    [Fact]
    public void pair_before_precheck_never_reaches_login_post()
    {
        var result = KioskCustomerLoginGate.Precheck(Guid.Empty, "cust", "secret-pass");
        Assert.Equal(KioskCustomerLoginPrecheck.PairBefore, result);
        Assert.Equal(KioskLoginStrings.PairBefore, KioskCustomerLoginGate.MessageFor(result));
        Assert.DoesNotContain("secret", KioskCustomerLoginGate.MessageFor(result));
    }

    [Fact]
    public void empty_fields_precheck_is_local_only()
    {
        var result = KioskCustomerLoginGate.Precheck(Guid.NewGuid(), "", "x");
        Assert.Equal(KioskCustomerLoginPrecheck.EmptyFields, result);
        Assert.Equal(KioskLoginStrings.EmptyFields, KioskCustomerLoginGate.MessageFor(result));
    }
}