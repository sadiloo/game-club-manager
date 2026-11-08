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

    public KioskRuntime(Func<HttpClient> httpFactory) => _httpFactory = httpFactory;

    public void Configure(Uri hubUri, Guid deviceId, string deviceKey, string stationId, string queuePath, bool legacyWindows)
    {
        ServerBase = hubUri.GetLeftPart(UriPartial.Authority);
        DeviceId = deviceId;
        DeviceKey = deviceKey;
        StationId = stationId;
        _queue = new SqliteOfflineEventQueue(queuePath);
        _hub = KioskConnectionFactory.Create(hubUri, deviceId.ToString(), deviceKey, legacyWindows);
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
        State = KioskRuntimeState.Playing;
    }

    public void BindSession(Guid sessionId) => _sessionId = sessionId;

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
                    if (ack.ManualSettle)
                    {
                        State = KioskRuntimeState.ManualSettle;
                    }
                }
            }
            catch (Exception ex)
            {
                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
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
