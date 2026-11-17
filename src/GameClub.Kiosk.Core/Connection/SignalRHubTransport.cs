using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Connection;

internal sealed class SignalRHubTransport : IKioskHubTransport
{
    private readonly HubConnection _hub;
    private volatile bool _signalRAutoReconnecting;

    public SignalRHubTransport(HubConnection hub)
    {
        _hub = hub;
        _hub.Reconnecting += _ =>
        {
            _signalRAutoReconnecting = true;
            Reconnecting?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            _signalRAutoReconnecting = false;
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Closed += _ =>
        {
            _signalRAutoReconnecting = false;
            return Task.CompletedTask;
        };
    }

    public HubConnectionState State => _hub.State;

    public bool SignalRAutoReconnecting => _signalRAutoReconnecting;

    public event Func<Exception?, Task>? Closed
    {
        add => _hub.Closed += value;
        remove => _hub.Closed -= value;
    }

    public event Action? Reconnecting;

    public event Action? Reconnected;

    public Task StartAsync(CancellationToken cancellationToken) => _hub.StartAsync(cancellationToken);

    public Task<T> InvokeAsync<T>(string methodName, object? arg1, CancellationToken cancellationToken)
        => _hub.InvokeAsync<T>(methodName, arg1, cancellationToken);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}