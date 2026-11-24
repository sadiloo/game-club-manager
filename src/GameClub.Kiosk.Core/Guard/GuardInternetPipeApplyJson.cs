using System;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Classifies pipe apply bodies using the same JSON shape as <see cref="GuardInternetClient.ApplyAsync"/>.</summary>
public static class GuardInternetPipeApplyJson
{
    public static bool IsStatusRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return string.Equals(
                doc.RootElement.GetProperty("verb").GetString(),
                "status",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Only pipe apply commands participate in held-request FIFO / firewall apply gate.</summary>
    public static bool RequiresMutatingApplyFifo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return string.Equals(
                doc.RootElement.GetProperty("verb").GetString(),
                "apply",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static bool IsOnApplyRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!string.Equals(doc.RootElement.GetProperty("verb").GetString(), "apply", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var command = JsonSerializer.Deserialize<SignedInternetCommand>(
                doc.RootElement.GetProperty("command").GetRawText());
            return command?.Payload.On ?? false;
        }
        catch
        {
            return false;
        }
    }
}