using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameClub.Kiosk.Core;

namespace GameClub.Kiosk;

public partial class MainWindow : Window
{
    private readonly KioskController _controller;
    private readonly DispatcherTimer _timer;
    private readonly WindowsGeneration _generation;
    private readonly string _databasePath;
    private NamedPipeClientStream? _pipe;
    private AdminGate? _admin;

    public MainWindow()
    {
        InitializeComponent();
        var version = Environment.OSVersion.Version;
        _generation = WindowsSupport.Generation(version, version.Build);
        _databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub", "kiosk.db");
        _controller = BuildController();
        _controller.Restore(WindowsSupport.TehranNow(DateTimeOffset.UtcNow));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        if (WindowsSupport.Classify(version) == KioskOsSupport.UpgradeWindows8To81)
        {
            LoginButton.IsEnabled = false;
            LockMessage.Text = "ویندوز ۸ باید به ۸.۱ ارتقا پیدا کند. NET Framework 4.8 روی ویندوز ۸ نصب نمی‌شود.";
        }

        Loaded += (_, _) => Render();
    }

    private KioskController BuildController()
    {
        var store = new LocalStore(_databasePath);
        var hub = ReadHubUrl();
        ICafeServer server;
        if (hub is string url && url.Length > 0)
        {
            server = new SignalRCafeServerClient(url, WindowsSupport.ClientWebSocketSupported(Environment.OSVersion.Version));
        }
        else
        {
            server = new ConnectionGate(DemoStand.CreateServer());
        }

        var hashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub", "admin-gate.json");
        if (File.Exists(hashPath))
        {
            _admin = new AdminGate(Pbkdf2Hash.FromJson(File.ReadAllText(hashPath)));
        }

        return new KioskController(server, store, DemoStand.Station, DemoStand.Rates(), DemoStand.HourlyToman);
    }

    private static string? ReadHubUrl()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kiosk.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        const string marker = "\"HubUrl\"";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var quote = text.IndexOf('"', text.IndexOf(':', at) + 1);
        var end = text.IndexOf('"', quote + 1);
        if (quote < 0 || end < 0)
        {
            return null;
        }

        var value = text.Substring(quote + 1, end - quote - 1).Trim();
        return value.Length == 0 ? null : value;
    }

    private void OnTick()
    {
        var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
        var wasLocked = _controller.Screen.Phase == KioskPhase.Locked;
        _controller.Tick(now);
        if (_controller.PendingServerLogout)
        {
            _ = _controller.FlushAsync();
        }

        SendHeartbeat(now);
        if (!wasLocked && _controller.Screen.Phase == KioskPhase.Locked)
        {
            WindowState = WindowState.Maximized;
            Topmost = true;
            Activate();
        }

        Render();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            var ok = await _controller.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password, WindowsSupport.TehranNow(DateTimeOffset.UtcNow));
            PasswordBox.Clear();
            if (!ok)
            {
                LockMessage.Text = _controller.Screen.Message ?? "";
            }
        }
        finally
        {
            LoginButton.IsEnabled = true;
            Render();
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await _controller.LogoutAsync(WindowsSupport.TehranNow(DateTimeOffset.UtcNow));
        WindowState = WindowState.Maximized;
        Topmost = true;
        Render();
    }

    private void Game_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string gameId)
        {
            return;
        }

        var plan = _controller.PrepareLaunch(gameId, _generation);
        if (!plan.Allowed || string.IsNullOrWhiteSpace(plan.FileName))
        {
            LockMessage.Text = plan.Error ?? "";
            return;
        }

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments ?? "",
                WorkingDirectory = string.IsNullOrWhiteSpace(plan.WorkingDirectory) ? "" : plan.WorkingDirectory,
                UseShellExecute = plan.UseShellExecute,
            };
            System.Diagnostics.Process.Start(info);
            Topmost = false;
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            LockMessage.Text = ex.Message;
        }

        Render();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F12)
        {
            AdminView.Visibility = Visibility.Visible;
            AdminPassword.Focus();
            e.Handled = true;
        }
    }

    private void AdminUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_admin is null)
        {
            AdminMessage.Text = "گذرواژهٔ مدیر هنوز تنظیم نشده است.";
            return;
        }

        var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
        if (!_admin.TryOpen(AdminPassword.Password, now, TimeSpan.FromMinutes(10)))
        {
            AdminMessage.Text = "گذرواژه نادرست است.";
            return;
        }

        AdminPassword.Clear();
        AdminView.Visibility = Visibility.Collapsed;
        Topmost = false;
        SendAdmin("admin-escape");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AdminMessage.Text = ex.Message;
        }
    }

    private void AdminCancel_Click(object sender, RoutedEventArgs e)
    {
        AdminView.Visibility = Visibility.Collapsed;
        AdminPassword.Clear();
    }

    private void Render()
    {
        var screen = _controller.Screen;
        var locked = screen.Phase == KioskPhase.Locked;
        LockView.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        PlayView.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (locked)
        {
            LockMessage.Text = screen.Message ?? LockMessage.Text;
            Topmost = true;
            return;
        }

        MemberName.Text = screen.MemberName;
        MoneyText.Text = screen.MoneyText;
        TimeText.Text = "زمان باقی‌مانده: " + screen.TimeText;
        GraceText.Visibility = screen.InGrace ? Visibility.Visible : Visibility.Collapsed;
        GraceText.Text = screen.InGrace ? "مهلت یک دقیقه — بعد از آن سیستم بسته می‌شود." : "";
        OfflineText.Visibility = screen.Offline ? Visibility.Visible : Visibility.Collapsed;
        GameList.Children.Clear();
        foreach (var game in screen.Games)
        {
            GameList.Children.Add(GameButton(game));
        }
    }

    private Button GameButton(LauncherGame game)
    {
        var panel = new StackPanel { Width = 180 };
        panel.Children.Add(Cover(game));
        panel.Children.Add(new TextBlock
        {
            Text = game.Name,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 16,
        });
        panel.Children.Add(new TextBlock
        {
            Text = game.Category,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 13,
        });
        var button = new Button
        {
            Content = panel,
            Tag = game.Id,
            Width = 210,
            Height = 180,
            Margin = new Thickness(8),
            Background = (Brush)FindResource("Card"),
            Foreground = (Brush)FindResource("Ink"),
            BorderBrush = (Brush)FindResource("Gold"),
        };
        button.Click += Game_Click;
        return button;
    }

    private static UIElement Cover(LauncherGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.CoverPath) && File.Exists(game.CoverPath))
        {
            return new Image
            {
                Source = new BitmapImage(new Uri(Path.GetFullPath(game.CoverPath))),
                Height = 96,
                Stretch = Stretch.UniformToFill,
            };
        }

        return new Border
        {
            Height = 96,
            Background = new SolidColorBrush(Color.FromRgb(32, 36, 52)),
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(game.Name) ? "؟" : game.Name.Substring(0, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 32,
            },
        };
    }

    private void SendHeartbeat(DateTimeOffset now)
    {
        if (_controller.Session.SessionId is null)
        {
            return;
        }

        Send(new PipeMessage
        {
            Kind = "heartbeat",
            SessionId = _controller.Session.SessionId,
            UnixMilliseconds = now.ToUnixTimeMilliseconds(),
        });
    }

    private void SendAdmin(string kind) => Send(new PipeMessage { Kind = kind, UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    private void Send(PipeMessage message)
    {
        try
        {
            _pipe ??= new NamedPipeClientStream(".", "GameClub.Kiosk", PipeDirection.Out, PipeOptions.Asynchronous);
            if (!_pipe.IsConnected)
            {
                _pipe.Connect(50);
            }

            var bytes = PipeFraming.Encode(message);
            _pipe.Write(bytes, 0, bytes.Length);
            _pipe.Flush();
        }
        catch (Exception)
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
