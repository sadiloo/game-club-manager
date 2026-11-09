using System.Diagnostics;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Runtime;

public enum KioskRuntimeState
{
    Unpaired,
    Locked,
    Playing,
    ManualSettle,
    Ended,
}

public sealed class KioskRuntime : IAsyncDisposable
{
    private readonly Func<HttpClient> _httpFactory;
    private HubConnection? _hub;
    private SqliteOfflineEventQueue? _queue;
    private CancellationTokenSource? _heartbeatCts;
    private Guid _sessionId;
    private readonly Stopwatch _monotonic = new();

    public KioskRuntimeState State { get; private set; } = KioskRuntimeState.Unpaired;
    public string ServerBase { get; private set; } = "";
    public string StationId { get; private set; } = "";
    public Guid DeviceId { get; private set; }
    public string DeviceKey { get; private set; } = "";

    public event Action<KioskHeartbeatAck>? HeartbeatAcknowledged;

    public event Action? HeartbeatTransportFailed;

    public long ClientMonotonicMs => _monotonic.ElapsedMilliseconds;

    public bool IsTransportConnected => _hub?.State == HubConnectionState.Connected;

    private long _lastTransportFailedSignalMs;

    public KioskRuntime(Func<HttpClient> httpFactory) => _httpFactory = httpFactory;

    public void Configure(
        Uri hubUri,
        Guid deviceId,
        string deviceKey,
        string stationId,
        string queuePath,
        bool legacyWindows,
        HttpMessageHandler? testHubHandler = null)
    {
        ServerBase = hubUri.GetLeftPart(UriPartial.Authority);
        DeviceId = deviceId;
        DeviceKey = deviceKey;
        StationId = stationId;
        _queue = new SqliteOfflineEventQueue(queuePath);
        _hub = KioskConnectionFactory.Create(hubUri, deviceId.ToString(), deviceKey, legacyWindows, testHubHandler);
        State = KioskRuntimeState.Locked;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_hub is null || _queue is null)
        {
            throw new InvalidOperationException("Kiosk runtime is not configured.");
        }

        await _hub.StartAsync(ct);
        _monotonic.Restart();
        _heartbeatCts = new CancellationTokenSource();
        _ = RunHeartbeatAsync(_heartbeatCts.Token);
        State = _sessionId == Guid.Empty ? KioskRuntimeState.Locked : KioskRuntimeState.Playing;
    }

    public void BindSession(Guid sessionId)
    {
        _sessionId = sessionId;
        if (_hub?.State == HubConnectionState.Connected && sessionId != Guid.Empty)
        {
            State = KioskRuntimeState.Playing;
        }
        else if (sessionId == Guid.Empty)
        {
            State = KioskRuntimeState.Locked;
        }
    }

    public SqliteOfflineEventQueue GetQueue()
    {
        if (_queue is null)
        {
            throw new InvalidOperationException("Kiosk runtime is not configured.");
        }

        return _queue;
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    var ack = await _hub.InvokeAsync<KioskHeartbeatAck>(
                        "Heartbeat",
                        new KioskHeartbeatRequest
                        {
                            SessionId = _sessionId == Guid.Empty ? null : _sessionId,
                            ClientMonotonicMs = _monotonic.ElapsedMilliseconds,
                        },
                        ct);
                    ApplyAcknowledgement(ack);
                    HeartbeatAcknowledged?.Invoke(ack);
                }
                else
                {
                    SignalTransportDownIfNeeded();
                }
            }
            catch (Exception ex)
            {
                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow);
                SignalTransportDownIfNeeded();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    public void SignalTransportDownIfNeeded()
    {
        if (_sessionId == Guid.Empty || _hub?.State == HubConnectionState.Connected)
        {
            return;
        }

        var ms = _monotonic.ElapsedMilliseconds;
        if (_lastTransportFailedSignalMs > 0 && ms - _lastTransportFailedSignalMs < 30_000)
        {
            return;
        }

        HeartbeatTransportFailed?.Invoke();
        _lastTransportFailedSignalMs = ms;
    }

    private void ApplyAcknowledgement(KioskHeartbeatAck ack)
    {
        if (ack.ManualSettle)
        {
            State = KioskRuntimeState.ManualSettle;
            return;
        }

        if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
        {
            if (ack.Lease?.Payload.SessionId is Guid leased && leased != Guid.Empty)
            {
                if (_sessionId != Guid.Empty && leased != _sessionId)
                {
                    State = KioskRuntimeState.Locked;
                    return;
                }

                if (_sessionId == Guid.Empty)
                {
                    _sessionId = leased;
                }
            }

            if (_sessionId != Guid.Empty)
            {
                State = KioskRuntimeState.Playing;
            }

            return;
        }

        if (string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase)
            || (_sessionId != Guid.Empty && !string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(ack.SessionStatus) && ack.SessionStatus != "Paused"))
        {
            State = KioskRuntimeState.Ended;
            return;
        }

        if (_sessionId == Guid.Empty)
        {
            State = KioskRuntimeState.Locked;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatCts?.Cancel();
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        _heartbeatCts?.Dispose();
    }
}
