#if NET48
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

[Trait("Category", "WindowsWpf")]
[Collection("windows-wpf-sta")]
public sealed class KioskHostStatusTimerTests
{
    private readonly WpfStaApplicationFixture _wpf;

    public KioskHostStatusTimerTests(WpfStaApplicationFixture wpf) => _wpf = wpf;

    [Fact]
    public void host_boot_path_shows_no_server_response_when_heartbeats_fail()
        => RunScenario(lateConnect: false, operatorStopLock: false);

    [Fact]
    public void host_late_connect_shows_no_server_response_when_heartbeats_fail()
        => RunScenario(lateConnect: true, operatorStopLock: false);

    [Fact]
    public void host_operator_stop_lock_shows_no_server_response_when_heartbeats_fail()
        => RunScenario(lateConnect: false, operatorStopLock: true);

    private void RunScenario(bool lateConnect, bool operatorStopLock)
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunScenarioOnDispatcherAsync(lateConnect, operatorStopLock);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, TimeSpan.FromMinutes(2));

        if (error is not null)
        {
            Assert.Fail(error.ToString());
        }
    }

    private static async Task RunScenarioOnDispatcherAsync(bool lateConnect, bool operatorStopLock)
    {
        WritePairedDeviceState("PC-HOST-D5");
        var host = CreateHost(out var status);
        try
        {
            await Task.Delay(300);
            Assert.True(IsUiTickEnabled(host), "paired boot must start the status UI tick");
            var runtime = GetRuntime(host);
            await runtime.DisposeAsync();
            var transport = new HostTestHubTransport(lateConnect);
            runtime.ConfigureForTests(
                transport,
                new SqliteOfflineEventQueue(Path.Combine(Path.GetTempPath(), $"host-d5-{Guid.NewGuid():N}.db")),
                "http://127.0.0.1:5999",
                Guid.NewGuid(),
                "device-key",
                "PC-HOST-D5");

            var ensureRuntime = typeof(KioskHostService).GetMethod("EnsureRuntimeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ensureRuntime);
            await (Task)ensureRuntime.Invoke(host, null);

            Assert.True(IsUiTickEnabled(host), "status UI tick must be running for paired boot");

            if (operatorStopLock)
            {
                var ack = new KioskHeartbeatAck
                {
                    SessionStatus = "AwaitingPayment",
                    ManualSettle = false,
                };
                typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { ack });
                Assert.True(IsUiTickEnabled(host), "status UI tick must stay on after operator-stop lock");
            }

            await WaitForTransportConnectedAsync(runtime, TimeSpan.FromSeconds(15));

            transport.HeartbeatHandler = _ => throw new InvalidOperationException("hb down");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var sawNoResponse = false;
            var sawBareConnected = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var text = status.Text ?? "";
                if (text.IndexOf("no server response", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sawNoResponse = true;
                }

                if (KioskStationStatus.ContainsConnectedWording(text))
                {
                    sawBareConnected = true;
                }
            }

            Assert.True(sawNoResponse, $"expected 'no server response' within timer window; last status={status.Text}");
            Assert.False(sawBareConnected, $"must not show bare connected while heartbeats fail; last status={status.Text}");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static KioskHostService CreateHost(out TextBlock status)
    {
        var window = new Window { Width = 400, Height = 300 };
        status = new TextBlock();
        var balance = new TextBlock();
        var elapsed = new TextBlock();
        var freeBand = new TextBlock();
        var lockPanel = new Border();
        var launcher = new Border();
        var playing = new Border();
        return new KioskHostService(window, status, balance, elapsed, freeBand, lockPanel, launcher, playing);
    }

    private static KioskRuntime GetRuntime(KioskHostService host)
    {
        var field = typeof(KioskHostService).GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic);
        return (KioskRuntime)field!.GetValue(host)!;
    }

    private static bool IsUiTickEnabled(KioskHostService host)
    {
        var tick = typeof(KioskHostService).GetField("_uiTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host) as DispatcherTimer;
        return tick!.IsEnabled;
    }

    private static void WritePairedDeviceState(string stationId)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(root);
        var deviceId = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            deviceId,
            deviceKey = "host-d5-key",
            stationId,
            serverBase = "http://127.0.0.1:5999",
            leaseVerifyPublicKeyPem = "",
        });
        File.WriteAllText(Path.Combine(root, "device.json"), json);
    }

    private static async Task WaitForTransportConnectedAsync(KioskRuntime runtime, TimeSpan timeout)
    {
        await runtime.WaitForTransportConnectedAsync(timeout, CancellationToken.None);
    }

    private sealed class HostTestHubTransport : IKioskHubTransport
    {
        private readonly bool _lateConnect;
        private int _starts;
        private HubConnectionState _state = HubConnectionState.Disconnected;

        public HostTestHubTransport(bool lateConnect) => _lateConnect = lateConnect;

        public Func<CancellationToken, Task<KioskHeartbeatAck>> HeartbeatHandler { get; set; }

        public HubConnectionState State => _state;

        public bool SignalRAutoReconnecting => false;

#pragma warning disable CS0067
        public event Func<Exception, Task> Closed;

        public event Action Reconnecting;

        public event Action Reconnected;
#pragma warning restore CS0067

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _starts++;
            if (_lateConnect && _starts == 1)
            {
                await Task.Delay(1500, cancellationToken);
                throw new InvalidOperationException("server down at boot");
            }

            _state = HubConnectionState.Connected;
            await Task.CompletedTask;
        }

        public async Task<T> InvokeAsync<T>(string methodName, object arg1, CancellationToken cancellationToken)
        {
            if (methodName != "Heartbeat")
            {
                throw new InvalidOperationException(methodName);
            }

            if (HeartbeatHandler is null)
            {
                return (T)(object)new KioskHeartbeatAck { SessionStatus = "Locked" };
            }

            var ack = await HeartbeatHandler(cancellationToken);
            return (T)(object)ack;
        }

        public ValueTask DisposeAsync()
        {
            _state = HubConnectionState.Disconnected;
            return default;
        }
    }
}
#endif