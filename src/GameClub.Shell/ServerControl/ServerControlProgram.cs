using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;
using GameClub.Shell.Shell;

namespace GameClub.Shell.ServerControl;

internal static class ServerControlProgram
{
    public static int Run(string verb)
    {
        try
        {
            if (string.Equals(verb, "stop", StringComparison.OrdinalIgnoreCase))
            {
                return RunStop();
            }

            if (string.Equals(verb, "start", StringComparison.OrdinalIgnoreCase))
            {
                return RunStart();
            }

            return 2;
        }
        catch
        {
            return 2;
        }
    }

    private static int RunStop()
    {
        var count = TryReadOpenSessions(out var readFailed);
        if (readFailed)
        {
            var d2 = ShellSeams.ShowMessageBox(
                "وضعیت جلسه‌های باز معلوم نیست. باز هم سرور متوقف شود؟",
                "توقف سرور",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (d2 != DialogResult.Yes)
            {
                return 1;
            }
        }
        else if (count > 0)
        {
            var digit = ServerStatePoller.ToPersianDigits(count.ToString());
            var d1 = ShellSeams.ShowMessageBox(
                $"{digit} جلسهٔ باز در جریان است. با توقف سرور، پنل اپراتور روی این رایانه و روی گوشی‌ها تا روشن شدن دوبارهٔ سرور باز نمی‌شود.",
                "توقف سرور",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (d1 != DialogResult.OK)
            {
                return 1;
            }
        }

        try
        {
            if (ShellSeams.Service.ServiceExists)
            {
                ShellSeams.Service.Stop();
            }
            else
            {
                return 2;
            }

            if (!ShellSeams.Service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)))
            {
                return 4;
            }

            return 0;
        }
        catch
        {
            return 2;
        }
    }

    private static int RunStart()
    {
        if (IsPortListening(5000))
        {
            ShellSeams.ShowMessageBox(
                "یک برنامهٔ دیگر روی این رایانه درگاه ۵۰۰۰ را گرفته و سرور چک‌پوینت نمی‌تواند روشن شود. آن برنامه را ببندید و از منوی آیکن چک‌پوینت «شروع سرور» را بزنید.",
                "برنامهٔ دیگری جای سرور را گرفته است",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            return 3;
        }

        try
        {
            if (!ShellSeams.Service.ServiceExists)
            {
                return 2;
            }

            ShellSeams.Service.Start();
            var deadline = ShellSeams.UtcNow().AddSeconds(90);
            while (ShellSeams.UtcNow() < deadline)
            {
                if (ShellSeams.Service.Status == ServiceControllerStatus.Running
                    && LocalServerStatusClient.TryRead(out var local)
                    && local!.DbOk)
                {
                    return 0;
                }

                System.Threading.Thread.Sleep(500);
            }

            return 4;
        }
        catch
        {
            return 2;
        }
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpClient();
            var result = listener.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            if (!success)
            {
                return false;
            }

            listener.EndConnect(result);
            listener.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int TryReadOpenSessions(out bool readFailed)
    {
        readFailed = true;
        if (ShellSeams.ReadOpenSessionCount is not null)
        {
            readFailed = false;
            return ShellSeams.ReadOpenSessionCount();
        }

        try
        {
            var apiKey = ReadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return 0;
            }

            var req = WebRequest.Create("http://127.0.0.1:5000/api/local/open-sessions");
            req.Method = "GET";
            req.Headers["X-Api-Key"] = apiKey;
            req.Timeout = 5000;
            using var resp = (HttpWebResponse)req.GetResponse();
            using var stream = resp.GetResponseStream();
            using var reader = new StreamReader(stream!);
            var json = JsonDocument.Parse(reader.ReadToEnd());
            readFailed = false;
            return json.RootElement.GetProperty("count").GetInt32();
        }
        catch
        {
            return 0;
        }
    }

    private static string? ReadApiKey()
    {
        try
        {
            if (!File.Exists(ShellPaths.ServerConfigPath))
            {
                return Environment.GetEnvironmentVariable("GAMECLUB_API_KEY");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(ShellPaths.ServerConfigPath));
            if (doc.RootElement.TryGetProperty("GAMECLUB_API_KEY", out var key))
            {
                return key.GetString();
            }
        }
        catch
        {
        }

        return Environment.GetEnvironmentVariable("GAMECLUB_API_KEY");
    }
}