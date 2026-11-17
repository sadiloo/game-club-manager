using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Connection;

internal interface IKioskHubTransport
{
    HubConnectionState State { get; }

    event Func<Exception?, Task>? Closed;

    event Action? Reconnecting;

    event Action? Reconnected;

    bool SignalRAutoReconnecting { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task<T> InvokeAsync<T>(string methodName, object? arg1, CancellationToken cancellationToken);

    ValueTask DisposeAsync();
}