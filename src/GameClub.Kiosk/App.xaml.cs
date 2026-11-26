using System.Net;
using System.Windows;
using GameClub.Kiosk.Themes;

namespace GameClub.Kiosk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        KioskThemeResources.ApplyInstallDefault();
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        base.OnStartup(e);
    }
}
