using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Kiosk;
using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Hubs;

public sealed class KioskHub(KioskCheckpointCoordinator coordinator) : Hub
{
    public async Task<KioskHeartbeatAck> Heartbeat(KioskHeartbeatRequest request)
    {
        if (!Context.GetHttpContext()!.Items.TryGetValue("KioskDeviceId", out var idObj) || idObj is not Guid deviceId)
        {
            throw new HubException("device_auth");
        }

        return await coordinator.OnHeartbeatAsync(deviceId, request.SessionId, request.ClientMonotonicMs, Context.ConnectionAborted);
    }
}
