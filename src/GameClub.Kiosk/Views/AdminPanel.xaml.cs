using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameClub.Kiosk.Core.Admin;
using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Views;

public partial class AdminPanel : UserControl
{
    public event EventHandler? ServerSettingsRequested;

    private readonly KioskAdminLockStore _lockStore = new();
    private readonly DispatcherTimer _lockTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private KioskAdminRole _role;
    private string _managerHash = "";
    private string _operatorHash = "";

    public AdminPanel()
    {
        InitializeComponent();
        _lockTick.Tick += (_, _) => UpdateLockUi();
        Loaded += (_, _) => ReloadHashes();
    }

    public void ShowPanel()
    {
        ReloadHashes();
        RoleStep.Visibility = Visibility.Visible;
        PasswordStep.Visibility = Visibility.Collapsed;
        MenuStep.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void HidePanel()
    {
        Visibility = Visibility.Collapsed;
        _lockTick.Stop();
    }

    private void ReloadHashes()
    {
        var setup = new KioskSetupStore().TryLoad();
        _managerHash = setup?.KioskManagerHash ?? "";
        _operatorHash = setup?.KioskOperatorHash ?? "";
    }

    private void OnPickOperator(object sender, RoutedEventArgs e) => BeginPasswordStep(KioskAdminRole.Operator, "رمز اپراتور کیوسک");

    private void OnPickManager(object sender, RoutedEventArgs e) => BeginPasswordStep(KioskAdminRole.Manager, "رمز مدیر کیوسک");

    private void BeginPasswordStep(KioskAdminRole role, string title)
    {
        _role = role;
        RolePicked?.Invoke(this, role);
        PasswordTitle.Text = title;
        AdminPasswordBox.Clear();
        AdminError.Visibility = Visibility.Collapsed;
        RoleStep.Visibility = Visibility.Collapsed;
        PasswordStep.Visibility = Visibility.Visible;
        MenuStep.Visibility = Visibility.Collapsed;
        UpdateLockUi();
        if (!_lockStore.Lock.IsLocked(role, DateTime.UtcNow, out _))
        {
            AdminPasswordBox.Focus();
        }
    }

    private void OnPasswordSubmit(object sender, RoutedEventArgs e)
    {
        var utc = DateTime.UtcNow;
        if (_lockStore.Lock.IsLocked(_role, utc, out var until) && until is not null)
        {
            ShowLockMessage(utc, until.Value);
            return;
        }

        var hash = _role == KioskAdminRole.Manager ? _managerHash : _operatorHash;
        var roleKey = _role == KioskAdminRole.Manager ? "manager" : "operator";
        if (!KioskCredentialVerifier.Verify(hash, AdminPasswordBox.Password, roleKey))
        {
            _lockStore.Lock.RegisterWrong(_role, utc);
            _lockStore.Save();
            AdminError.Visibility = Visibility.Visible;
            AdminError.Text = "رمز اشتباه است";
            AdminPasswordBox.Clear();
            UpdateLockUi();
            return;
        }

        _lockStore.Lock.RegisterSuccess(_role);
        _lockStore.Save();
        ShowMenu();
    }

    private void ShowMenu()
    {
        PasswordStep.Visibility = Visibility.Collapsed;
        MenuStep.Visibility = Visibility.Visible;
        RestartMenuButton.Visibility = Visibility.Visible;
        var isManager = _role == KioskAdminRole.Manager;
        var isOperator = _role == KioskAdminRole.Operator;
        OperatorLockButton.Visibility = isOperator ? Visibility.Visible : Visibility.Collapsed;
        OperatorUnlockButton.Visibility = isOperator ? Visibility.Visible : Visibility.Collapsed;
        ServerSettingsButton.Visibility = isManager ? Visibility.Visible : Visibility.Collapsed;
        ExitWindowsButton.Visibility = isManager ? Visibility.Visible : Visibility.Collapsed;
        MenuTitle.Text = isManager ? "منوی مدیر کیوسک" : "منوی اپراتور کیوسک";
    }

    private void UpdateLockUi()
    {
        var utc = DateTime.UtcNow;
        if (PasswordStep.Visibility != Visibility.Visible)
        {
            _lockTick.Stop();
            return;
        }

        if (_lockStore.Lock.IsLocked(_role, utc, out var until) && until is not null)
        {
            ShowLockMessage(utc, until.Value);
            AdminPasswordBox.IsEnabled = false;
            _lockTick.Start();
        }
        else
        {
            LockCountdown.Visibility = Visibility.Collapsed;
            AdminPasswordBox.IsEnabled = true;
            _lockTick.Stop();
        }
    }

    private void ShowLockMessage(DateTime utc, DateTime until)
    {
        var seconds = KioskAdminLock.RemainingSeconds(utc, until);
        LockCountdown.Visibility = Visibility.Visible;
        LockCountdown.Text = $"زمان باقی‌مانده: {seconds / 60:D2}:{seconds % 60:D2}";
        AdminError.Visibility = Visibility.Visible;
        AdminError.Text = "به‌خاطر ۲ رمز اشتباه، تا ۱۵ دقیقه قفل است";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => HidePanel();

    public event EventHandler<KioskAdminRole>? RolePicked;

    public event EventHandler? OperatorLockRequested;

    public event EventHandler? OperatorUnlockRequested;

    private void OnOperatorLock(object sender, RoutedEventArgs e) => OperatorLockRequested?.Invoke(this, EventArgs.Empty);

    private void OnOperatorUnlock(object sender, RoutedEventArgs e) => OperatorUnlockRequested?.Invoke(this, EventArgs.Empty);

    public void ShowOperatorError(string errorKey)
    {
        AdminError.Visibility = Visibility.Visible;
        AdminError.Text = errorKey switch
        {
            KioskOperatorLockRules.OfflineSessionLockErrorKey => KioskOpStrings.OplockErrOffline,
            KioskOperatorLockRules.OfflineFreeUseStartErrorKey => KioskOpStrings.FreeuseErrOffline,
            "admin.pcLock.err.stale" => KioskOpStrings.AdminPcLockErrStale,
            _ => KioskOpStrings.AdminPcLockErrGeneric,
        };
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        HidePanel();
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    private void OnServerSettings(object sender, RoutedEventArgs e)
    {
        HidePanel();
        ServerSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitWindows(object sender, RoutedEventArgs e)
    {
        HidePanel();
        Application.Current.Shutdown();
    }
}