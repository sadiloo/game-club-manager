using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Tests.Fakes;

internal sealed class FakeKioskHubTransport : IKioskHubTransport
{
    private HubConnectionState _state = HubConnectionState.Disconnected;
    private int _startAttempt;

    public int StartCallCount { get; private set; }

    public int HeartbeatCallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool SignalRAutoReconnecting { get; private set; }

    public IList<TimeSpan> RecordedDelays { get; } = new List<TimeSpan>();

    public Func<int, CancellationToken, Task>? StartHandler { get; set; }

    public Func<CancellationToken, Task<KioskHeartbeatAck>>? HeartbeatHandler { get; set; }

    public HubConnectionState State => _state;

    public event Func<Exception?, Task>? Closed;

    public event Action? Reconnecting;

    public event Action? Reconnected;

    public void SimulateSignalRReconnecting()
    {
        SignalRAutoReconnecting = true;
        Reconnecting?.Invoke();
    }

    public void SimulateSignalRReconnected()
    {
        SignalRAutoReconnecting = false;
        Reconnected?.Invoke();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StartCallCount++;
        _startAttempt++;
        if (StartHandler is not null)
        {
            await StartHandler(_startAttempt, cancellationToken);
        }

        _state = HubConnectionState.Connected;
    }

    public async Task<T> InvokeAsync<T>(string methodName, object? arg1, CancellationToken cancellationToken)
    {
        if (methodName == "Heartbeat")
        {
            HeartbeatCallCount++;
            if (HeartbeatHandler is not null)
            {
                var ack = await HeartbeatHandler(cancellationToken);
                return (T)(object)ack;
            }

            return (T)(object)new KioskHeartbeatAck { SessionStatus = "Locked" };
        }

        throw new InvalidOperationException($"Unexpected hub method {methodName}.");
    }

    public void ForceClose(Exception? error = null)
    {
        _state = HubConnectionState.Disconnected;
        var handler = Closed;
        if (handler is not null)
        {
            _ = handler(error);
        }
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _state = HubConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }
}