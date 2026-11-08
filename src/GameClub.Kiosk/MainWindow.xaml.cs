using System.Windows;
using System.Windows.Input;
using GameClub.Kiosk.Services;

namespace GameClub.Kiosk;

public partial class MainWindow : Window
{
    private readonly KioskHostService _host;

    public MainWindow()
    {
        InitializeComponent();
        _host = new KioskHostService(this, StatusText, DisplayBalance, DisplayPackage, DisplayElapsed, DisplayFreeBand, LockPanel, PlayingPanel);
        ServerUrlBox.Text = _host.ServerBase;
        KeyDown += OnKeyDown;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            _host.SetServerBase("http://127.0.0.1:5000");
        }
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ServerUrlBox.Text))
        {
            _host.SetServerBase(ServerUrlBox.Text.Trim());
        }

        await _host.SubmitPairingCodeAsync(PairingCodeBox.Text.Trim());
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
        => await _host.LoginAsync(LoginNameBox.Text.Trim(), PasswordBox.Password);

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
        => await _host.LogoutAsync();

    public Task PairAsync(string code) => _host.SubmitPairingCodeAsync(code);
}
