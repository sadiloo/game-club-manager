namespace GameClub.Kiosk.Core.Login;

public enum KioskCustomerLoginPrecheck
{
    Ok,
    EmptyFields,
    PairBefore,
}

public static class KioskCustomerLoginGate
{
    public static KioskCustomerLoginPrecheck Precheck(Guid deviceId, string loginName, string password)
    {
        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(password))
        {
            return KioskCustomerLoginPrecheck.EmptyFields;
        }

        if (deviceId == Guid.Empty)
        {
            return KioskCustomerLoginPrecheck.PairBefore;
        }

        return KioskCustomerLoginPrecheck.Ok;
    }

    public static string MessageFor(KioskCustomerLoginPrecheck result)
        => result switch
        {
            KioskCustomerLoginPrecheck.EmptyFields => KioskLoginStrings.EmptyFields,
            KioskCustomerLoginPrecheck.PairBefore => KioskLoginStrings.PairBefore,
            _ => "",
        };
}