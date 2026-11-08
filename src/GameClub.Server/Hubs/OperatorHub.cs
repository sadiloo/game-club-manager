using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Hubs;

public sealed class OperatorHub : Hub
{
    public const string GridRefreshEvent = "gridRefresh";
}

public interface IOperatorPanelNotifier
{
    Task NotifyGridRefreshAsync(CancellationToken ct = default);
}

public sealed class OperatorPanelNotifier(IHubContext<OperatorHub> hub) : IOperatorPanelNotifier
{
    public Task NotifyGridRefreshAsync(CancellationToken ct = default)
        => hub.Clients.All.SendAsync(OperatorHub.GridRefreshEvent, cancellationToken: ct);
}
