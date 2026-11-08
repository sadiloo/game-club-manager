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

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            if (_host.TryUnlockServerUrlEdit(AdminUrlPasswordBox.Password))
            {
                ServerUrlBox.IsReadOnly = false;
                ServerUrlBox.Focus();
            }
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
