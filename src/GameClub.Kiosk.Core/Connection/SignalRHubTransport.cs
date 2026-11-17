using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Connection;

internal sealed class SignalRHubTransport : IKioskHubTransport
{
    private readonly HubConnection _hub;

    public SignalRHubTransport(HubConnection hub) => _hub = hub;

    public HubConnectionState State => _hub.State;

    public event Func<Exception?, Task>? Closed
    {
        add => _hub.Closed += value;
        remove => _hub.Closed -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _hub.StartAsync(cancellationToken);

    public Task<T> InvokeAsync<T>(string methodName, object? arg1, CancellationToken cancellationToken)
        => _hub.InvokeAsync<T>(methodName, arg1, cancellationToken);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}