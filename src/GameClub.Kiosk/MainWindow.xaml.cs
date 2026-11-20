using System.Windows;
using System.Windows.Input;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Setup;
using GameClub.Kiosk.Services;

namespace GameClub.Kiosk;

public partial class MainWindow : Window
{
    private readonly KioskHostService _host;

    public MainWindow()
    {
        InitializeComponent();
        KioskStatusInlineApplicator.Apply(
            StatusText,
            KioskStatusPresentationBuilder.Build(KioskStatusLineState.Unpaired(), null, null));
        _host = new KioskHostService(
            this,
            StatusText,
            LoginError,
            DisplayBalance,
            DisplayElapsed,
            DisplayFreeBand,
            LockPanel,
            LauncherPanel,
            PlayingPanel,
            GameList,
            GameSearchBox,
            GameCategoryBox);
        ServerUrlBox.Text = _host.ServerBase;
        var setupStore = new KioskSetupStore();
        if (setupStore.ShouldShowFirstRun())
        {
            LockPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Collapsed;
            PlayingPanel.Visibility = Visibility.Collapsed;
            FooterText.Visibility = Visibility.Collapsed;
            FirstRunSetup.Visibility = Visibility.Visible;
            FirstRunSetup.SetupCompleted += OnFirstRunSetupCompleted;
        }
        else if (setupStore.TryLoad()?.IsComplete == true)
        {
            AdminUrlPasswordBox.Visibility = Visibility.Collapsed;
            FooterText.Text = "F9: safe test exit";
        }

        AdminPanel.ServerSettingsRequested += (_, _) =>
        {
            FirstRunSetup.Visibility = Visibility.Visible;
            LockPanel.Visibility = Visibility.Collapsed;
        };

        AdminPanel.RolePicked += (_, role) => _host.NotifyAdminRole(role);

        AdminPanel.OperatorLockRequested += async (_, _) =>
        {
            var err = await _host.TryOperatorLockAsync();
            if (err is not null)
            {
                AdminPanel.ShowOperatorError(err);
            }
            else
            {
                AdminPanel.HidePanel();
            }
        };

        AdminPanel.OperatorUnlockRequested += async (_, _) =>
        {
            var err = await _host.TryOperatorUnlockAsync();
            if (err is not null)
            {
                AdminPanel.ShowOperatorError(err);
            }
            else
            {
                AdminPanel.HidePanel();
            }
        };

        GameCategoryBox.Items.Add("All");
        GameCategoryBox.Items.Add("1");
        GameCategoryBox.Items.Add("2");
        GameCategoryBox.SelectedIndex = 0;
        KeyDown += OnKeyDown;
        RefreshThemeToggleVisibility();
        LoginNameBox.PreviewKeyDown += (_, _) => _host.DismissLoginErrorMessage();
        LoginNameBox.TextChanged += (_, _) => _host.DismissLoginErrorMessage();
        PasswordBox.PreviewKeyDown += (_, _) => _host.DismissLoginErrorMessage();
        PasswordBox.PasswordChanged += (_, _) => _host.DismissLoginErrorMessage();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            if (KioskLegacyDevPath.IsActive())
            {
                if (_host.TryUnlockServerUrlEdit(AdminUrlPasswordBox.Password))
                {
                    ServerUrlBox.IsReadOnly = false;
                    ServerUrlBox.Focus();
                }
            }
            else if (new KioskSetupStore().TryLoad()?.IsComplete == true)
            {
                AdminPanel.ShowPanel();
            }
        }
        else if (e.Key == Key.F9 && _host.TrySafeTestExit(AdminUrlPasswordBox.Password))
        {
            Application.Current.Shutdown();
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

    private void OnGameDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GameList.SelectedIndex >= 0)
        {
            _host.LaunchGameAt(GameList.SelectedIndex);
        }
    }

    public Task PairAsync(string code) => _host.SubmitPairingCodeAsync(code);

    private void RefreshThemeToggleVisibility()
    {
        var show = LockPanel.Visibility == Visibility.Visible;
        ThemeToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            ThemeToggle.RefreshChrome();
        }
    }

    private void OnFirstRunSetupCompleted(object? sender, EventArgs e)
    {
        FirstRunSetup.Visibility = Visibility.Collapsed;
        var setup = new KioskSetupStore().TryLoad();
        if (setup is not null && !string.IsNullOrEmpty(setup.ServerBase))
        {
            _host.SetServerBase(setup.ServerBase);
            ServerUrlBox.Text = setup.ServerBase;
        }

        LockPanel.Visibility = Visibility.Visible;
        FooterText.Visibility = Visibility.Visible;
        AdminUrlPasswordBox.Visibility = Visibility.Collapsed;
        FooterText.Text = "F8: مدیریت کیوسک · F9: safe test exit";
        RefreshThemeToggleVisibility();
    }
}
