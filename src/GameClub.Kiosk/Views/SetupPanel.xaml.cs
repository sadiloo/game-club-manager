using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Views;

public partial class SetupPanel : UserControl
{
    public event EventHandler? SetupCompleted;

    private readonly HttpClient _http = new();
    private readonly KioskSetupStore _store = new();
    private string _serverBase = "";

    public SetupPanel()
    {
        InitializeComponent();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (!KioskServerUrlNormalizer.TryNormalize(ServerUrlInput.Text, out var normalized, out var code))
        {
            Step1Error.Visibility = Visibility.Visible;
            Step1Error.Text = code == "empty"
                ? "آدرس سرور را وارد کنید."
                : "این آدرس درست نیست. نمونه: 192.168.1.10";
            return;
        }

        Step1Error.Visibility = Visibility.Collapsed;
        ConnectButton.IsEnabled = false;
        try
        {
            var response = await _http.GetAsync($"{normalized}/api/kiosk/setup/hello");
            if (!response.IsSuccessStatusCode)
            {
                ShowStep1Retry();
                return;
            }

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("product").GetString() != "Checkpoint")
            {
                ShowStep1Retry();
                return;
            }

            _serverBase = normalized;
            ServerUrlCaption.Text = $"سرور: {_serverBase}";
            Step1Panel.Visibility = Visibility.Collapsed;
            Step2Panel.Visibility = Visibility.Visible;
            StepCaption.Text = "مرحلهٔ ۲ از ۲";
            ManagerPasswordInput.Focus();
        }
        catch (HttpRequestException ex)
        {
            if (KioskServerTransportErrors.IsTimeout(ex))
            {
                ShowServerTimeout();
            }
            else
            {
                ShowServerUnreachable();
            }
        }
        catch (TaskCanceledException)
        {
            ShowServerTimeout();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void ShowStep1Retry()
    {
        Step1Error.Visibility = Visibility.Visible;
        Step1Error.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.WarningBrush");
        Step1Error.Text = "سرور پیدا نشد؛ دوباره تلاش می‌شود…";
    }

    private void ShowServerUnreachable()
    {
        Step1Error.Visibility = Visibility.Visible;
        Step1Error.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.DangerBrush");
        Step1Error.Text = KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ServerUnreachable);
    }

    private void ShowServerTimeout()
    {
        Step1Error.Visibility = Visibility.Visible;
        Step1Error.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.WarningBrush");
        Step1Error.Text = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Timeout);
    }

    private async void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ManagerPasswordInput.Password))
        {
            Step2Notice.Visibility = Visibility.Visible;
            Step2Notice.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.DangerBrush");
            Step2Notice.Text = "رمز را وارد کنید.";
            return;
        }

        ContinueButton.IsEnabled = false;
        try
        {
            var payload = JsonSerializer.Serialize(new { password = ManagerPasswordInput.Password });
            var response = await _http.PostAsync(
                $"{_serverBase}/api/kiosk/setup/verify-admin-password",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Step2Notice.Visibility = Visibility.Visible;
                Step2Notice.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.DangerBrush");
                Step2Notice.Text = "رمز اشتباه است";
                ManagerPasswordInput.Clear();
                ManagerPasswordInput.Focus();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Step2Notice.Visibility = Visibility.Visible;
                Step2Notice.Foreground = (System.Windows.Media.Brush)FindResource("Kiosk.WarningBrush");
                Step2Notice.Text = "سرور پاسخ نداد یا رمز هنوز ساخته نشده است.";
                return;
            }

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _store.Save(new KioskSetupDocument
            {
                ServerBase = _serverBase,
                KioskManagerHash = body.RootElement.GetProperty("kioskManagerHash").GetString() ?? "",
                KioskOperatorHash = body.RootElement.GetProperty("kioskOperatorHash").GetString() ?? "",
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
            SetupCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (HttpRequestException ex)
        {
            Step1Panel.Visibility = Visibility.Visible;
            Step2Panel.Visibility = Visibility.Collapsed;
            StepCaption.Text = "مرحلهٔ ۱ از ۲";
            if (KioskServerTransportErrors.IsTimeout(ex))
            {
                ShowServerTimeout();
            }
            else
            {
                ShowServerUnreachable();
            }
        }
        catch (TaskCanceledException)
        {
            Step1Panel.Visibility = Visibility.Visible;
            Step2Panel.Visibility = Visibility.Collapsed;
            StepCaption.Text = "مرحلهٔ ۱ از ۲";
            ShowServerTimeout();
        }
        finally
        {
            ContinueButton.IsEnabled = true;
        }
    }
}