#if NET48
using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Runtime;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

[Trait("Category", "WindowsWpf")]
[Collection("windows-wpf-sta")]
public sealed class WindowsKioskWpfTransportTests
{
    private readonly WpfStaApplicationFixture _wpf;

    public WindowsKioskWpfTransportTests(WpfStaApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public void wpf_funded_setup_top_up_and_credential_must_succeed()
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfLiveServerTestHelper.SetupFundedSessionAsync("topupcred", "pass").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(2));
        Assert.Null(error);
        Assert.NotNull(WpfLiveServerTestHelper.LastTopUpStatus);
        Assert.NotNull(WpfLiveServerTestHelper.LastCredentialStatus);
    }

    [Fact]
    public void wpf_login_waits_for_transport_connected()
    {
        var setupError = (Exception)null;
        var setupThread = new Thread(() =>
        {
            try
            {
                WpfLiveServerTestHelper.SetupFundedSessionAsync("transportlogin", "pass").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                setupError = ex;
            }
        });
        setupThread.Start();
        setupThread.Join(TimeSpan.FromMinutes(2));
        if (setupError is not null)
        {
            Assert.Fail(setupError.ToString());
        }

        Guid? sessionId = null;
        Exception inProcessError = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                var window = new GameClub.Kiosk.MainWindow();
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var host = window.GetType().GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    await WpfLiveServerTransportHelper.WaitForKioskRuntimeTransportConnectedAsync(host, TimeSpan.FromSeconds(60));
                    var login = host.GetType().GetMethod("LoginAsync", BindingFlags.Instance | BindingFlags.Public);
                    await (Task)login.Invoke(host, new object[] { "transportlogin", "pass" });
                    sessionId = await LookupOpenSessionIdAsync("transportlogin");
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                inProcessError = ex;
            }
        }, TimeSpan.FromMinutes(2));

        if (inProcessError is not null)
        {
            Assert.Fail(inProcessError.ToString());
        }

        Assert.NotNull(sessionId);
        Assert.NotEqual(Guid.Empty, sessionId.Value);
    }

    private static async Task<Guid> LookupOpenSessionIdAsync(string loginName)
    {
        var connString = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE");
        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        // Same normalization as GameClub.Server.Kiosk.KioskCrypto.NormalizeLoginName (Trim + ToUpperInvariant).
        var normalizedLogin = loginName.Trim().ToUpperInvariant();
        using var cmd = new NpgsqlCommand(
            """
            SELECT s."Id"
            FROM sessions s
            INNER JOIN customer_credentials cc ON cc."CustomerId" = s."CustomerId"
            WHERE cc."LoginNameNormalized" = @login AND s."Status" = 'Open'
            ORDER BY s."StartedAtUtc" DESC
            LIMIT 1
            """,
            conn);
        cmd.Parameters.AddWithValue("login", normalizedLogin);
        var value = await cmd.ExecuteScalarAsync();
        return value is Guid g ? g : Guid.Empty;
    }
}
#endif