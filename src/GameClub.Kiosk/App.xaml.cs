using System.Net;
using System.Windows;

namespace GameClub.Kiosk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        base.OnStartup(e);
    }
}
