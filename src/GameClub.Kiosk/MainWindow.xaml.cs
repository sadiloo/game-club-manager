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
        _host = new KioskHostService(this, StatusText);
        KeyDown += OnKeyDown;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            _host.SetServerBase("http://127.0.0.1:5000");
        }
    }

    public Task PairAsync(string code) => _host.SubmitPairingCodeAsync(code);
}
