namespace GameClub.Billing;

/// <summary>Stable <see cref="BillingException.Code"/> values. Messages can change; these strings do not.</summary>
public static class BillingErrors
{
    public const string InvalidTariff = "invalid_tariff";
    public const string TariffNotFound = "tariff_not_found";
    public const string InvalidMoney = "invalid_money";
    public const string InvalidTime = "invalid_time";
    public const string InvalidDate = "invalid_date";
    public const string InvalidSession = "invalid_session";
    public const string PcPause = "pc_pause";
    public const string InvalidAccount = "invalid_account";
    public const string InvalidPayment = "invalid_payment";
    public const string InvalidInvoice = "invalid_invoice";
    public const string InvalidPolicy = "invalid_policy";
    public const string InvalidLevel = "invalid_level";
    public const string InvalidTopUp = "invalid_topup";
    public const string NotEligible = "not_eligible";
    public const string OutsideWindow = "outside_window";
}
