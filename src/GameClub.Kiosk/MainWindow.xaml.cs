using System.Diagnostics;
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
    private DispatcherTimer? _relock;
    private InMemoryCafeServer? _demo;
    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private TimeSpan _marked;
    private bool _allowClose;

    private void AllowServiceClose() => _allowClose = true;

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
        Closing += (_, args) => args.Cancel = ShellClosePolicy.CancelClose(_allowClose);
        _ = _allowClose;
        PreviewKeyDown += (_, args) =>
        {
            if (ShellClosePolicy.SwallowAltF4(args.Key == Key.System && args.SystemKey == Key.F4, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
            {
                args.Handled = true;
            }
        };
    }

    private KioskController BuildController()
    {
        var store = new LocalStore(_databasePath);
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kiosk.json");
        var json = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
#if DEBUG
        var debugBuild = true;
#else
        var debugBuild = false;
#endif
        var startup = KioskStartup.Decide(json, debugBuild, Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase)));
        ICafeServer server;
        if (!startup.Run)
        {
            server = new RefusalServer(startup.LockMessage);
        }
        else if (startup.Demo)
        {
            _demo = DemoStand.CreateServer();
            _demo.SetServerClock(WindowsSupport.TehranNow(DateTimeOffset.UtcNow));
            server = new ConnectionGate(_demo);
        }
        else
        {
            server = new SignalRCafeServerClient(
                startup.HubUrl!,
                WindowsSupport.ClientWebSocketSupported(Environment.OSVersion.Version),
                startup.CaThumbprint,
                startup.StationToken);
        }

        return new KioskController(server, store, DemoStand.Station, DemoStand.Rates(), 0);
    }

    private void OnTick()
    {
        var elapsed = _mono.Elapsed - _marked;
        _marked = _mono.Elapsed;
        var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
        _demo?.SetServerClock(now);
        var wasLocked = _controller.Screen.Phase == KioskPhase.Locked;
        _controller.AdvanceMonotonic(elapsed, now);
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
            var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
            _demo?.SetServerClock(now);
            var ok = await _controller.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password, now);
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
        var password = AdminPassword.Password;
        var verdict = SendPrivileged("admin-escape", password);
        if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
        {
            AdminMessage.Text = verdict == "no_admin"
                ? "گذرواژهٔ مدیر هنوز تنظیم نشده است."
                : verdict == "rejected"
                    ? "گذرواژه نادرست است."
                    : "محافظ ورود مدیر را نپذیرفت.";
            return;
        }

        AdminPassword.Clear();
        AdminView.Visibility = Visibility.Collapsed;
        Topmost = false;
        _relock?.Stop();
        _relock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _relock.Tick += (_, _) =>
        {
            _relock?.Stop();
            SendPrivileged("admin-relock", "");
        };
        _relock.Start();
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

    private string? SendPrivileged(string kind, string password)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "GameClub.Kiosk", PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(1000);
            var bytes = PipeFraming.Encode(new PipeMessage
            {
                Kind = kind,
                Password = password,
                UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            var buffer = new byte[4096];
            var read = pipe.Read(buffer, 0, buffer.Length);
            if (!PipeFraming.TryDecode(buffer, read, out var message, out _))
            {
                return null;
            }

            return message?.Note;
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    private sealed class RefusalServer : ICafeServer
    {
        private readonly string _message;

        public RefusalServer(string message) => _message = string.IsNullOrWhiteSpace(message) ? "پیکربندی کیوسک پیدا نشد." : message;

        public Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.FromResult(LoginResult.Fail(_message));

        public Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken) =>
            Task.FromResult(new LogoutResult { Offline = true, Message = _message });

        public Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameEntry>>([]);
    }
}
