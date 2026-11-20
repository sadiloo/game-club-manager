#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
        => RunScenario(lateConnect: true, operatorStopLock: false);

    [Fact]
    public void host_late_connect_shows_no_server_response_when_heartbeats_fail()
        => RunScenario(lateConnect: true, operatorStopLock: false);

    [Fact]
    public void host_connected_starts_status_tick_without_refresh_async()
        => RunConnectedTickScenario();

    [Fact]
    public void host_open_session_no_display_shows_no_server_response_when_heartbeats_fail()
        => RunOpenSessionNoDisplayScenario();

    [Fact]
    public void host_replay_tick_starts_independently_when_playing_resumes()
        => RunReplayTickIndependenceScenario();

    [Fact]
    public void host_operator_stop_lock_shows_no_server_response_when_heartbeats_fail()
        => RunScenario(lateConnect: false, operatorStopLock: true);

    [Fact]
    public void host_ack_lock_then_open_ack_same_session_stays_locked()
        => RunD7Scenario(leaseExpiredLock: false);

    [Fact]
    public void host_lease_expired_lock_then_open_ack_same_session_stays_locked()
        => RunD7Scenario(leaseExpiredLock: true);

    [Fact]
    public void host_ack_lock_shows_locked_prefix_with_connection_state()
        => RunD6LockTextScenario(leaseExpired: false);

    [Fact]
    public void host_lease_expired_lock_shows_locked_prefix_with_connection_state()
        => RunD6LockTextScenario(leaseExpired: true);

    [Fact]
    public void host_logout_shows_locked_prefix_with_connection_state()
        => RunD6LogoutLockTextScenario();

    [Fact]
    public void host_top_up_open_ack_after_lease_lock_resumes_playing()
        => RunD7TopUpUnlockScenario();

    [Fact]
    public void host_local_lock_keeps_session_id_bound()
        => RunD7SessionIdStaysBoundScenario();

    [Fact]
    public void host_local_lock_shows_layout_when_session_cleanup_throws()
        => RunLockSurvivesCleanupFailureScenario();

    [Fact]
    public void host_logout_shows_layout_when_session_cleanup_throws()
        => RunLogoutSurvivesCleanupFailureScenario();

    [Fact]
    public void host_logout_shows_layout_when_logout_http_fails()
        => RunLogoutSurvivesLogoutHttpFailureScenario();

    [Fact]
    public void host_logout_open_ack_during_slow_post_stays_locked_without_session()
        => RunLogoutOpenAckDuringSlowPostScenario();

    [Fact]
    public void host_logout_session_adopt_during_slow_post_relocks_after_await()
        => RunLogoutSessionAdoptDuringSlowPostRelockScenario();

    [Fact]
    public void host_logout_login_during_slow_post_readopts_new_session_on_next_ack()
        => RunLogoutLoginDuringSlowPostReAdoptScenario();

    [Fact]
    public void host_logout_failed_http_re_adopts_open_session_on_next_ack()
        => RunLogoutFailedHttpReAdoptScenario();

    [Fact]
    public void host_logout_http_409_re_adopts_open_session_on_next_ack()
        => RunLogoutHttp409ReAdoptScenario();

    [Fact]
    public void host_logout_real_http_409_re_adopts_open_session_on_next_ack()
        => RunLogoutRealHttp409ReAdoptScenario();

    [Fact]
    public void host_logout_http_503_re_adopts_open_session_on_next_ack()
        => RunLogoutHttp503ReAdoptScenario();

    [Fact]
    public void host_login_timeout_shows_lock_line_timeout_error_through_ui_tick()
        => RunLoginTimeoutLockLineScenario();

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

    private void RunD7Scenario(bool leaseExpiredLock)
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunD7OnDispatcherAsync(leaseExpiredLock);
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

    private void RunConnectedTickScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunConnectedTickOnDispatcherAsync();
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

    private void RunReplayTickIndependenceScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunReplayTickIndependenceOnDispatcherAsync();
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

    private void RunOpenSessionNoDisplayScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunOpenSessionNoDisplayOnDispatcherAsync();
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

    private void RunD6LockTextScenario(bool leaseExpired)
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunD6LockTextOnDispatcherAsync(leaseExpired);
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
        var host = CreateHost(out var status, out var lockPanel, out var playingPanel);
        try
        {
            await Task.Delay(300);
            Assert.True(IsUiTickEnabled(host), "paired boot must start the status UI tick before transport connects");
            var runtime = GetRuntime(host);
            await runtime.ResetForReconfigureAsync();
            var transport = new HostTestHubTransport(lateConnect);
            runtime.ConfigureForTests(
                transport,
                new SqliteOfflineEventQueue(Path.Combine(Path.GetTempPath(), $"host-d5-{Guid.NewGuid():N}.db")),
                "http://127.0.0.1:5999",
                Guid.NewGuid(),
                "device-key",
                "PC-HOST-D5");

            await runtime.StartAsync(CancellationToken.None);
            if (!lateConnect)
            {
                await WaitForTransportConnectedAsync(runtime, TimeSpan.FromSeconds(15));
            }

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
                Assert.Contains("Locked —", status.Text ?? "", StringComparison.Ordinal);
            }

            if (lateConnect)
            {
                await WaitForTransportConnectedAsync(runtime, TimeSpan.FromSeconds(30));
            }

            transport.HeartbeatHandler = _ => throw new InvalidOperationException("hb down");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var sawNoResponse = false;
            var sawBareConnected = false;
            var pastFailureThreshold = false;
            var staleConnectedWatchActive = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var text = status.Text ?? "";
                if (text.IndexOf("no server response", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sawNoResponse = true;
                    if (operatorStopLock)
                    {
                        staleConnectedWatchActive = true;
                    }
                    else
                    {
                        pastFailureThreshold = true;
                    }
                }

                if (operatorStopLock)
                {
                    if (staleConnectedWatchActive && KioskStationStatus.ContainsConnectedWording(text))
                    {
                        sawBareConnected = true;
                    }
                }
                else
                {
                    var failures = (int)runtime.GetType()
                        .GetField("_consecutiveHeartbeatFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(runtime)!;
                    if (failures >= 2)
                    {
                        pastFailureThreshold = true;
                    }

                    if (pastFailureThreshold && KioskStationStatus.ContainsConnectedWording(text))
                    {
                        sawBareConnected = true;
                    }
                }
            }

            Assert.True(sawNoResponse, $"expected 'no server response' within timer window; last status={status.Text}");
            var finalText = status.Text ?? "";
            Assert.Contains("no server response", finalText, StringComparison.OrdinalIgnoreCase);
            Assert.False(sawBareConnected, $"must not show bare connected while heartbeats fail; last status={finalText}");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunD7OnDispatcherAsync(bool leaseExpiredLock)
    {
        WritePairedDeviceState("PC-HOST-D7");
        var host = CreateHost(out var status, out var lockPanel, out var playingPanel);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            var runtime = GetRuntime(host);
            await runtime.ResetForReconfigureAsync();
            var transport = new HostTestHubTransport(lateConnect: false);
            runtime.ConfigureForTests(
                transport,
                new SqliteOfflineEventQueue(Path.Combine(Path.GetTempPath(), $"host-d7-{Guid.NewGuid():N}.db")),
                "http://127.0.0.1:5999",
                Guid.NewGuid(),
                "device-key",
                "PC-HOST-D7");
            runtime.BindSession(sessionId);

            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (leaseExpiredLock)
            {
                onAck.Invoke(host, new object[]
                {
                    new KioskHeartbeatAck
                    {
                        SessionStatus = "Open",
                        Lease = new SignedSessionLease
                        {
                            Payload = new SessionLeasePayload { SessionId = sessionId, StationId = "PC-HOST-D7" },
                        },
                    },
                });
            }
            else
            {
                onAck.Invoke(host, new object[] { new KioskHeartbeatAck { SessionStatus = "AwaitingPayment" } });
            }

            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            var locallyLocked = (Guid)GetField(host, "_locallyLockedSessionId")!;
            Assert.Equal(sessionId, locallyLocked);

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-D7",
                        DateTime.UtcNow.AddMinutes(-1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 0 },
                },
            });

            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_sessionId"));
            Assert.Equal(sessionId, runtime.GetType().GetField("_sessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
            Assert.Contains("Locked —", status.Text ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunD7TopUpUnlockScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunD7TopUpUnlockOnDispatcherAsync();
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

    private static async Task RunD7TopUpUnlockOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-D7-TU");
        var host = CreateHost(out _, out var lockPanel, out var playingPanel);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            var runtime = GetRuntime(host);
            await runtime.ResetForReconfigureAsync();
            runtime.ConfigureForTests(
                new HostTestHubTransport(lateConnect: false),
                new SqliteOfflineEventQueue(Path.Combine(Path.GetTempPath(), $"host-d7-tu-{Guid.NewGuid():N}.db")),
                "http://127.0.0.1:5999",
                Guid.NewGuid(),
                "device-key",
                "PC-HOST-D7-TU");
            runtime.BindSession(sessionId);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-D7-TU",
                        DateTime.UtcNow.AddMinutes(-5)),
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-D7-TU",
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 2),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });

            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_locallyLockedSessionId"));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunD7SessionIdStaysBoundScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunD7SessionIdStaysBoundOnDispatcherAsync();
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

    private static async Task RunD7SessionIdStaysBoundOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-D7-SID");
        var host = CreateHost(out _, out _, out _);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            var runtime = GetRuntime(host);
            runtime.BindSession(sessionId);
            typeof(KioskHostService).GetMethod("EnterLocalLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, new object[] { true });
            Assert.Equal(sessionId, GetField(host, "_sessionId"));
            Assert.Equal(sessionId, GetField(host, "_locallyLockedSessionId"));
            Assert.Equal(sessionId, runtime.GetType().GetField("_sessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunLockSurvivesCleanupFailureScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLockSurvivesCleanupFailureOnDispatcherAsync();
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

    private static async Task RunLockSurvivesCleanupFailureOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-CLEANUP");
        var host = CreateHost(out _, out var lockPanel, out var playingPanel, new KioskHostTestSeam { SessionCleanupThrows = true });
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            typeof(KioskHostService).GetMethod("EnterLocalLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, new object[] { false });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunLogoutSurvivesCleanupFailureScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutSurvivesCleanupFailureOnDispatcherAsync();
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

    private void RunLogoutSurvivesLogoutHttpFailureScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutSurvivesLogoutHttpFailureOnDispatcherAsync();
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

    private static async Task RunLogoutSurvivesLogoutHttpFailureOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-LOGOUT-HTTP");
        var seam = new KioskHostTestSeam { LogoutHttpThrows = true };
        var host = CreateHost(out _, out var lockPanel, out var playingPanel, seam);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            await host.LogoutAsync();
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunLogoutOpenAckDuringSlowPostScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutOpenAckDuringSlowPostOnDispatcherAsync();
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

    private void RunLogoutSessionAdoptDuringSlowPostRelockScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutSessionAdoptDuringSlowPostRelockOnDispatcherAsync();
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

    private static async Task RunLogoutOpenAckDuringSlowPostOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-LOGOUT-RACE");
        var sessionId = Guid.NewGuid();
        var seam = new KioskHostTestSeam
        {
            LogoutHttpDelay = TimeSpan.FromMilliseconds(800),
            SimulateSuccessfulLogoutHttp = true,
        };
        var host = CreateHost(out var status, out var lockPanel, out var playingPanel, seam);
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-LOGOUT-RACE",
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            var logoutTask = host.LogoutAsync();
            await Task.Delay(50);
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_locallyLockedSessionId"));
            Assert.True((bool)GetField(host, "_logoutInProgress")!);
            await Task.Delay(50);
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-LOGOUT-RACE",
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 2),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.True((bool)GetField(host, "_logoutInProgress")!);
            var pollEnd = DateTime.UtcNow.AddMilliseconds(700);
            while (DateTime.UtcNow < pollEnd && !logoutTask.IsCompleted)
            {
                Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
                await Task.Delay(50);
            }

            await logoutTask;
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_sessionId"));
            var text = status.Text ?? "";
            Assert.StartsWith("Locked — PC-HOST-LOGOUT-RACE · ", text, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunLogoutSessionAdoptDuringSlowPostRelockOnDispatcherAsync()
    {
        const string stationId = "PC-HOST-LOGOUT-ADOPT";
        WritePairedDeviceState(stationId);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var seam = new KioskHostTestSeam
        {
            LogoutHttpDelay = TimeSpan.FromMilliseconds(800),
            LogoutHttpSimulatedStatusCode = 503,
        };
        var host = CreateHost(out var status, out var lockPanel, out var playingPanel, seam);
        try
        {
            SetField(host, "_sessionId", sessionA);
            GetRuntime(host).BindSession(sessionA);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionA,
                        stationId,
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);

            var logoutTask = host.LogoutAsync();
            await Task.Delay(50);
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionA, GetField(host, "_locallyLockedSessionId"));
            Assert.True((bool)GetField(host, "_logoutInProgress")!);

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionB,
                        stationId,
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionB, GetField(host, "_sessionId"));

            await logoutTask;
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_sessionId"));
            Assert.Equal(Guid.Empty, GetField(host, "_locallyLockedSessionId"));
            Assert.False((bool)GetField(host, "_logoutInProgress")!);
            var text = status.Text ?? "";
            Assert.StartsWith($"Locked — {stationId} · ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("no server response", text, StringComparison.OrdinalIgnoreCase);

            var settleEnd = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < settleEnd)
            {
                await Task.Delay(250);
                Assert.Equal(Visibility.Visible, lockPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
                var refreshed = status.Text ?? "";
                Assert.StartsWith($"Locked — {stationId} · ", refreshed, StringComparison.Ordinal);
            }
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunLogoutLoginDuringSlowPostReAdoptScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutLoginDuringSlowPostReAdoptOnDispatcherAsync();
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

    private static async Task RunLogoutLoginDuringSlowPostReAdoptOnDispatcherAsync()
    {
        const string stationId = "PC-HOST-LOGOUT-LOGIN-SLOW";
        WritePairedDeviceState(stationId);
        var sessionS1 = Guid.NewGuid();
        var sessionS2 = Guid.NewGuid();
        var handler = new SlowLogoutLoginDuringPostHandler(sessionS1, sessionS2, TimeSpan.FromMilliseconds(600));
        var host = CreateHost(out _, out var lockPanel, out var playingPanel);
        var httpField = typeof(KioskHostService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(httpField);
        if (httpField.GetValue(host) is HttpClient replaced)
        {
            replaced.Dispose();
        }

        var httpClient = new HttpClient(handler);
        httpField.SetValue(host, httpClient);
        typeof(KioskHostService).GetMethod("ApplyDeviceHeaders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object[] { httpClient });
        try
        {
            SetField(host, "_sessionId", sessionS1);
            GetRuntime(host).BindSession(sessionS1);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionS1,
                        stationId,
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);

            var logoutTask = host.LogoutAsync();
            await Task.Delay(50);
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionS1, GetField(host, "_locallyLockedSessionId"));
            Assert.True((bool)GetField(host, "_logoutInProgress")!);

            await host.LoginAsync("cust-slow-logout", "pass");
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            Assert.Equal(sessionS2, GetField(host, "_sessionId"));
            Assert.NotEqual(sessionS1, GetField(host, "_sessionId"));
            Assert.True((bool)GetField(host, "_logoutInProgress")!);

            await logoutTask;
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_sessionId"));
            Assert.False((bool)GetField(host, "_logoutInProgress")!);
            Assert.Equal(1, handler.LogoutPostCount);
            Assert.NotEqual(sessionS1, GetField(host, "_sessionId"));

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionS2,
                        stationId,
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 2),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionS2, GetField(host, "_sessionId"));
            Assert.NotEqual(sessionS1, GetField(host, "_sessionId"));

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionS2,
                        stationId,
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 3),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            Assert.Equal(sessionS2, GetField(host, "_sessionId"));
            Assert.NotEqual(sessionS1, GetField(host, "_sessionId"));
        }
        finally
        {
            httpClient.Dispose();
            handler.Dispose();
            await host.DisposeAsync();
        }
    }

    private void RunLogoutFailedHttpReAdoptScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutFailedHttpReAdoptOnDispatcherAsync(null);
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

    private void RunLogoutHttp409ReAdoptScenario()
        => RunLogoutHttpStatusReAdoptScenario(409);

    private void RunLogoutRealHttp409ReAdoptScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutRealHttp409ReAdoptOnDispatcherAsync();
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

    private void RunLogoutHttp503ReAdoptScenario()
        => RunLogoutHttpStatusReAdoptScenario(503);

    private void RunLogoutHttpStatusReAdoptScenario(int statusCode)
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLogoutFailedHttpReAdoptOnDispatcherAsync(statusCode);
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

    private static async Task RunLogoutRealHttp409ReAdoptOnDispatcherAsync()
    {
        const string stationId = "PC-HOST-LOGOUT-REAL-HTTP";
        WritePairedDeviceState(stationId);
        var sessionId = Guid.NewGuid();
        var handler = new LogoutConflictCountingHandler(TimeSpan.FromMilliseconds(400));
        var host = CreateHost(out _, out var lockPanel, out var playingPanel);
        var httpField = typeof(KioskHostService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(httpField);
        if (httpField.GetValue(host) is HttpClient replaced)
        {
            replaced.Dispose();
        }

        var httpClient = new HttpClient(handler);
        httpField.SetValue(host, httpClient);
        typeof(KioskHostService).GetMethod("ApplyDeviceHeaders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object[] { httpClient });
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        stationId,
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);

            var logoutTask = host.LogoutAsync();
            await Task.Delay(50);
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_locallyLockedSessionId"));
            Assert.True((bool)GetField(host, "_logoutInProgress")!);

            var duringEnd = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < duringEnd && !logoutTask.IsCompleted)
            {
                Assert.Equal(Visibility.Visible, lockPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
                await Task.Delay(50);
            }

            await logoutTask;
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_sessionId"));
            Assert.Equal(Guid.Empty, GetField(host, "_locallyLockedSessionId"));
            Assert.False((bool)GetField(host, "_logoutInProgress")!);
            Assert.Equal(1, handler.PostCount);

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        stationId,
                        DateTime.UtcNow.AddMinutes(-1)),
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_sessionId"));

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        stationId,
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 2),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_sessionId"));
        }
        finally
        {
            httpClient.Dispose();
            handler.Dispose();
            await host.DisposeAsync();
        }
    }

    private static async Task RunLogoutFailedHttpReAdoptOnDispatcherAsync(int? simulatedHttpStatus)
    {
        WritePairedDeviceState("PC-HOST-LOGOUT-READOPT");
        var sessionId = Guid.NewGuid();
        var seam = simulatedHttpStatus is int code
            ? new KioskHostTestSeam { LogoutHttpSimulatedStatusCode = code }
            : new KioskHostTestSeam { LogoutHttpThrows = true };
        var host = CreateHost(out _, out var lockPanel, out var playingPanel, seam);
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            await host.LogoutAsync();
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Guid.Empty, GetField(host, "_sessionId"));
            Assert.Equal(Guid.Empty, GetField(host, "_locallyLockedSessionId"));
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-LOGOUT-READOPT",
                        DateTime.UtcNow.AddMinutes(-1)),
                },
            });
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_sessionId"));
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        sessionId,
                        "PC-HOST-LOGOUT-READOPT",
                        DateTime.UtcNow.AddHours(1),
                        leaseVersion: 2),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 3600 },
                },
            });
            Assert.Equal(Visibility.Collapsed, lockPanel.Visibility);
            Assert.Equal(Visibility.Visible, playingPanel.Visibility);
            Assert.Equal(sessionId, GetField(host, "_sessionId"));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunLogoutSurvivesCleanupFailureOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-LOGOUT-CLEANUP");
        var seam = new KioskHostTestSeam { SessionCleanupThrows = true, SkipLogoutHttp = true };
        var host = CreateHost(out _, out var lockPanel, out var playingPanel, seam);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            await host.LogoutAsync();
            Assert.Equal(Visibility.Visible, lockPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, playingPanel.Visibility);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private void RunD6LogoutLockTextScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunD6LogoutLockTextOnDispatcherAsync();
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

    private static async Task RunD6LogoutLockTextOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-LOGOUT");
        var host = CreateHost(out var status, out _, out _, new KioskHostTestSeam { SkipLogoutHttp = true });
        try
        {
            SetField(host, "_sessionId", Guid.NewGuid());
            await host.LogoutAsync();
            var text = status.Text ?? "";
            Assert.StartsWith("Locked — PC-HOST-LOGOUT · ", text, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunConnectedTickOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-CONN-TICK");
        var host = CreateHost(out _, out _, out _);
        try
        {
            var tick = typeof(KioskHostService).GetField("_uiTick", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host) as DispatcherTimer;
            tick!.Stop();
            Assert.False(tick.IsEnabled, "test setup requires status tick off before Connected");

            typeof(KioskHostService).GetMethod("OnRuntimeConnectionStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, new object[] { KioskTransportPhase.Connected, "" });

            Assert.True(tick.IsEnabled, "status tick must start on transport Connected without RefreshAsync");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunReplayTickIndependenceOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-REPLAY");
        var host = CreateHost(out _, out _, out _);
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", oldSession);
            GetRuntime(host).BindSession(oldSession);
            var uiTick = typeof(KioskHostService).GetField("_uiTick", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host) as DispatcherTimer;
            var replayTick = typeof(KioskHostService).GetField("_replayTick", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host) as DispatcherTimer;
            uiTick!.Start();
            replayTick!.Stop();
            Assert.True(uiTick.IsEnabled);
            Assert.False(replayTick.IsEnabled);

            SetField(host, "_locallyLockedSessionId", oldSession);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        newSession,
                        "PC-HOST-REPLAY",
                        DateTime.UtcNow.AddMinutes(-1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 120 },
                },
            });
            Assert.False(replayTick.IsEnabled, "replay tick must wait for verified lease on the next ack");

            onAck.Invoke(host, new object[]
            {
                new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Lease = HostTestLeaseSigning.SignLease(
                        newSession,
                        "PC-HOST-REPLAY",
                        DateTime.UtcNow.AddHours(1)),
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 120 },
                },
            });

            Assert.True(replayTick.IsEnabled, "replay tick must start when playing resumes even if status tick was already on");
            Assert.True(uiTick.IsEnabled);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunOpenSessionNoDisplayOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-O4D");
        var host = CreateHost(out var status, out _, out _);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            var runtime = GetRuntime(host);
            await runtime.ResetForReconfigureAsync();
            runtime.ConfigureForTests(
                new HostTestHubTransport(lateConnect: false) { HeartbeatHandler = _ => throw new InvalidOperationException("hb down") },
                new SqliteOfflineEventQueue(Path.Combine(Path.GetTempPath(), $"host-o4d-{Guid.NewGuid():N}.db")),
                "http://127.0.0.1:5999",
                Guid.NewGuid(),
                "device-key",
                "PC-HOST-O4D");
            await runtime.StartAsync(CancellationToken.None);
            runtime.BindSession(sessionId);
            runtime.GetType().GetField("_consecutiveHeartbeatFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(runtime, 3);
            SetField(host, "_lastHeartbeatAckUtc", DateTime.UtcNow.AddMinutes(-5));

            typeof(KioskHostService).GetMethod("OnRuntimeConnectionStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, new object[] { KioskTransportPhase.Connected, "" });

            typeof(KioskHostService).GetMethod("RefreshStationStatusText", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null);
            var text = status.Text ?? "";
            Assert.Contains("no server response", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connecting", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunD6LockTextOnDispatcherAsync(bool leaseExpired)
    {
        WritePairedDeviceState("PC-HOST-D6");
        var host = CreateHost(out var status, out _, out _);
        var sessionId = Guid.NewGuid();
        try
        {
            SetField(host, "_sessionId", sessionId);
            GetRuntime(host).BindSession(sessionId);
            var onAck = typeof(KioskHostService).GetMethod("OnHeartbeatAcknowledged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (leaseExpired)
            {
                onAck.Invoke(host, new object[]
                {
                    new KioskHeartbeatAck
                    {
                        SessionStatus = "Open",
                        Lease = new SignedSessionLease
                        {
                            Payload = new SessionLeasePayload { SessionId = sessionId, StationId = "PC-HOST-D6" },
                        },
                    },
                });
                var text = status.Text ?? "";
                Assert.Contains("Locked — lease expired (PC-HOST-D6)", text, StringComparison.Ordinal);
                Assert.Contains(" · ", text, StringComparison.Ordinal);
            }
            else
            {
                onAck.Invoke(host, new object[] { new KioskHeartbeatAck { SessionStatus = "AwaitingPayment" } });
                var text = status.Text ?? "";
                Assert.Contains("Locked — PC-HOST-D6", text, StringComparison.Ordinal);
                Assert.DoesNotContain("lease expired", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(" · ", text, StringComparison.Ordinal);
            }

            Assert.False(KioskStationStatus.ContainsConnectedWording(status.Text ?? ""));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static KioskHostService CreateHost(
        out TextBlock status,
        out UIElement lockPanel,
        out UIElement playingPanel,
        KioskHostTestSeam testSeam = null)
    {
        var window = new Window { Width = 400, Height = 300 };
        status = new TextBlock();
        var balance = new TextBlock();
        var elapsed = new TextBlock();
        var freeBand = new TextBlock();
        lockPanel = new Border();
        var launcher = new Border();
        playingPanel = new Border();
        return new KioskHostService(window, status, balance, elapsed, freeBand, lockPanel, launcher, playingPanel, testSeam: testSeam);
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

    private static void SetField(object host, string name, object value)
    {
        typeof(KioskHostService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
    }

    private static object GetField(object host, string name)
        => typeof(KioskHostService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

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
            leaseVerifyPublicKeyPem = HostTestLeaseSigning.PublicKeyPem,
        });
        File.WriteAllText(Path.Combine(root, "device.json"), json);
    }

    [Fact]
    public void host_profile_cleanup_skips_reparse_point_links()
        => RunProfileCleanupSkipsReparsePointScenario();

    [Fact]
    public void host_profile_cleanup_skips_reparse_point_scratch_root()
        => RunProfileCleanupSkipsReparseScratchRootScenario();

    private void RunProfileCleanupSkipsReparsePointScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunProfileCleanupSkipsReparsePointOnDispatcherAsync();
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

    private void RunProfileCleanupSkipsReparseScratchRootScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunProfileCleanupSkipsReparseScratchRootOnDispatcherAsync();
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

    private void RunLoginTimeoutLockLineScenario()
    {
        Exception error = null;
        _wpf.RunAsync(async () =>
        {
            try
            {
                await RunLoginTimeoutLockLineOnDispatcherAsync();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, TimeSpan.FromMinutes(1));

        if (error is not null)
        {
            Assert.Fail(error.ToString());
        }
    }

    private static async Task RunLoginTimeoutLockLineOnDispatcherAsync()
    {
        WritePairedDeviceState("PC-HOST-TIMEOUT");
        var host = CreateHost(
            out var status,
            out _,
            out _,
            new KioskHostTestSeam { LoginHttpSimulateTimeout = true });
        try
        {
            SetField(host, "_stationId", "PC-HOST-TIMEOUT");
            SetField(host, "_deviceId", Guid.NewGuid());
            typeof(KioskHostService).GetMethod("ShowLockLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, null);
            await host.LoginAsync("timeout-user", "s3cret-pass");
            var text = status.Text ?? "";
            Assert.Contains(KioskLoginUserMessages.Timeout, text, StringComparison.Ordinal);
            Assert.Contains(" · ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret-pass", text, StringComparison.Ordinal);

            typeof(KioskHostService).GetMethod("RefreshStationStatusText", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(host, null);
            var refreshed = status.Text ?? "";
            Assert.Contains(KioskLoginUserMessages.Timeout, refreshed, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task RunProfileCleanupSkipsReparseScratchRootOnDispatcherAsync()
    {
        var protectedDir = Path.Combine(Path.GetTempPath(), $"host-reparse-protected-root-{Guid.NewGuid():N}");
        var profileRoot = Path.Combine(Path.GetTempPath(), $"host-reparse-profile-{Guid.NewGuid():N}");
        var scratchRel = "Downloads";
        Directory.CreateDirectory(protectedDir);
        Directory.CreateDirectory(profileRoot);
        File.WriteAllText(Path.Combine(protectedDir, "keep.txt"), "keep");
        CreateDirectoryJunction(Path.Combine(profileRoot, scratchRel), protectedDir);
        try
        {
            var clear = typeof(KioskHostService).GetMethod(
                "ClearKioskProfileScratchUnder",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            clear.Invoke(null, new object[] { profileRoot, new[] { scratchRel } });
            Assert.True(File.Exists(Path.Combine(protectedDir, "keep.txt")), "reparse scratch root must not empty link target");
        }
        finally
        {
            if (Directory.Exists(profileRoot))
            {
                foreach (var d in Directory.EnumerateDirectories(profileRoot))
                {
                    Directory.Delete(d, false);
                }

                Directory.Delete(profileRoot, false);
            }

            if (Directory.Exists(protectedDir))
            {
                Directory.Delete(protectedDir, true);
            }

            await Task.CompletedTask;
        }
    }

    private static async Task RunProfileCleanupSkipsReparsePointOnDispatcherAsync()
    {
        var protectedDir = Path.Combine(Path.GetTempPath(), $"host-reparse-protected-{Guid.NewGuid():N}");
        var scratchRoot = Path.Combine(Path.GetTempPath(), $"host-reparse-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(protectedDir);
        File.WriteAllText(Path.Combine(protectedDir, "keep.txt"), "keep");
        Directory.CreateDirectory(scratchRoot);
        CreateDirectoryJunction(Path.Combine(scratchRoot, "linked"), protectedDir);
        File.WriteAllText(Path.Combine(scratchRoot, "local.txt"), "delete");
        try
        {
            var cleanup = typeof(KioskHostService).GetMethod(
                "DeleteTreeContentsBestEffort",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            cleanup.Invoke(null, new object[] { scratchRoot });
            Assert.True(File.Exists(Path.Combine(protectedDir, "keep.txt")), "reparse target must survive cleanup");
            Assert.False(File.Exists(Path.Combine(scratchRoot, "local.txt")), "scratch file must be removed");
        }
        finally
        {
            if (Directory.Exists(scratchRoot))
            {
                foreach (var f in Directory.EnumerateFiles(scratchRoot))
                {
                    File.Delete(f);
                }

                foreach (var d in Directory.EnumerateDirectories(scratchRoot))
                {
                    Directory.Delete(d, false);
                }

                Directory.Delete(scratchRoot, false);
            }

            if (Directory.Exists(protectedDir))
            {
                Directory.Delete(protectedDir, true);
            }

            await Task.CompletedTask;
        }
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("mklink failed to start");
        proc.WaitForExit(10_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink /J failed with exit {proc.ExitCode}");
        }
    }

    private static async Task WaitForTransportConnectedAsync(KioskRuntime runtime, TimeSpan timeout)
    {
        await runtime.WaitForTransportConnectedAsync(timeout, CancellationToken.None);
    }

    private sealed class LogoutConflictCountingHandler : HttpMessageHandler
    {
        private readonly TimeSpan? _delay;

        public LogoutConflictCountingHandler(TimeSpan? delay = null) => _delay = delay;

        public int PostCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri is not null
                && request.RequestUri.AbsolutePath.EndsWith("/logout", StringComparison.OrdinalIgnoreCase))
            {
                PostCount++;
                if (_delay is TimeSpan d)
                {
                    await Task.Delay(d, cancellationToken);
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"error\":\"session_busy\"}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class SlowLogoutLoginDuringPostHandler : HttpMessageHandler
    {
        private readonly Guid _logoutSessionId;
        private readonly Guid _loginSessionId;
        private readonly TimeSpan _logoutDelay;

        public SlowLogoutLoginDuringPostHandler(Guid logoutSessionId, Guid loginSessionId, TimeSpan logoutDelay)
        {
            _logoutSessionId = logoutSessionId;
            _loginSessionId = loginSessionId;
            _logoutDelay = logoutDelay;
        }

        public int LogoutPostCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path.Contains($"/sessions/{_logoutSessionId}/logout", StringComparison.OrdinalIgnoreCase))
            {
                LogoutPostCount++;
                await Task.Delay(_logoutDelay, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                var body = $"{{\"sessionId\":\"{_loginSessionId}\"}}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get && path.Contains("/stations/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"isTestMode\":true}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get && path.Contains("/catalog", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"catalogVersion\":0,\"entries\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
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