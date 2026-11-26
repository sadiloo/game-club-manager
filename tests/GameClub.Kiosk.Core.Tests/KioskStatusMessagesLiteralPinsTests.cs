using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskStatusMessagesLiteralPinsTests
{
    [Fact]
    public void connection_status_literals_match_production_catalog()
    {
        Assert.Equal("وصل به سرور", KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ConnConnected));
        Assert.Equal("قطع از سرور", KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ConnNotConnected));
        Assert.Equal("سرور پاسخ نمی‌دهد", KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ConnNoResponse));
        Assert.Equal("در حال اتصال دوباره…", KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ConnReconnecting));
    }
}