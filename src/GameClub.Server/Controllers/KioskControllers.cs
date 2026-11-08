using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/kiosk")]
public sealed class KioskController(
    KioskPairingService pairing,
    KioskSessionService sessions,
    KioskReconcileService reconcile,
    GameCatalogService catalog,
    KioskProfileService profiles,
    AppDbContext db) : ControllerBase
{
    public sealed record PairRequest(string Code);

    [HttpPost("pair")]
    public async Task<IActionResult> Pair(PairRequest body, CancellationToken ct)
        => Ok(await pairing.PairAsync(body.Code, ct));

    [HttpGet("stations/{stationId}/config")]
    public async Task<IActionResult> StationConfig(string stationId, CancellationToken ct)
    {
        KioskDeviceAuthorization.EnsureStationMatches(HttpContext, stationId);
        var profile = await profiles.EnsureProfileAsync(stationId, ct);
        return Ok(new
        {
            isTestMode = profile.IsTestMode,
            nearEndMinutes = profiles.LoadNearEndMinutes(profile),
            profile.AlertSoundId,
        });
    }

    public sealed record LoginRequest(string LoginName, string Password, string StationId);

    [HttpPost("auth/login")]
    public async Task<IActionResult> Login(LoginRequest body, CancellationToken ct)
    {
        KioskDeviceAuthorization.EnsureStationMatches(HttpContext, body.StationId);
        var (customerId, session) = await sessions.LoginAndAttachAsync(body.LoginName, body.Password, body.StationId, ct);
        return Ok(new { customerId, sessionId = session.Id, status = session.Status.ToString() });
    }

    [HttpPost("sessions/{sessionId:guid}/logout")]
    public async Task<IActionResult> Logout(Guid sessionId, CancellationToken ct)
    {
        if (!HttpContext.Items.TryGetValue("KioskStationId", out var sid) || sid is not string stationId)
        {
            return Unauthorized();
        }

        await sessions.LogoutAsync(sessionId, stationId, ct);
        return NoContent();
    }

    [HttpGet("stations/{stationId}/active-session")]
    public async Task<IActionResult> ActiveSession(string stationId, CancellationToken ct)
    {
        KioskDeviceAuthorization.EnsureStationMatches(HttpContext, stationId);
        var id = StationIdNormalizer.Normalize(stationId);
        var session = await db.Sessions.AsNoTracking()
            .Where(s => s.StationId == id && s.Status == SessionStatus.Open)
            .OrderByDescending(s => s.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            return NotFound();
        }

        return Ok(new { sessionId = session.Id, session.CustomerId, prepaidKind = session.PrepaidKind.ToString() });
    }

    [HttpPost("devices/events")]
    public async Task<IActionResult> ReconcileEvents(IReadOnlyList<KioskOutboundEvent> body, CancellationToken ct)
    {
        if (!HttpContext.Items.TryGetValue("KioskDeviceId", out var idObj) || idObj is not Guid deviceId)
        {
            return Unauthorized();
        }

        var acked = await reconcile.ApplyEventsAsync(deviceId, body, ct);
        return Ok(new { ackedEventIds = acked });
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] long sinceVersion, CancellationToken ct)
        => Ok(await catalog.GetCatalogAsync(sinceVersion, ct));
}

[ApiController]
[Route("api/operator")]
public sealed class KioskOperatorController(
    KioskPairingService pairing,
    KioskProfileService profiles,
    KioskCustomerAuthService auth,
    KioskSessionService sessions,
    GameCatalogService catalog,
    SessionService sessionService,
    OperatorContext op,
    AuditService audit,
    AppDbContext db) : ControllerBase
{
    [HttpPost("stations/{stationId}/pairing-code")]
    public async Task<IActionResult> PairingCode(string stationId, CancellationToken ct)
    {
        EnsureOperator();
        return Ok(await pairing.CreatePairingCodeAsync(stationId, ct));
    }

    public sealed record KioskProfileRequest(bool IsTestMode, int[]? NearEndMinutes, string? AlertSoundId);

    [HttpPut("stations/{stationId}/kiosk-profile")]
    public async Task<IActionResult> KioskProfile(string stationId, KioskProfileRequest body, CancellationToken ct)
    {
        EnsureManager();
        var profile = await profiles.EnsureProfileAsync(stationId, ct);
        profile.IsTestMode = body.IsTestMode;
        if (body.NearEndMinutes is { Length: > 0 })
        {
            profile.NearEndMinutesJson = System.Text.Json.JsonSerializer.Serialize(body.NearEndMinutes);
        }

        profile.AlertSoundId = body.AlertSoundId;
        await audit.AppendAsync("kiosk.profile", new { stationId, profile.IsTestMode, actor = op.UserId }, ct);
        await db.SaveChangesAsync(ct);
        return Ok(new { profile.StationId, profile.IsTestMode });
    }

    [HttpPost("sessions/{sessionId:guid}/force-logout")]
    public async Task<IActionResult> ForceLogout(Guid sessionId, CancellationToken ct)
    {
        EnsureOperator();
        await sessionService.StopAsync(sessionId, ct);
        return NoContent();
    }

    public sealed record StartGuestBody(
        PrepaidSessionKind? PrepaidKind,
        int? Minutes,
        long? AmountToman,
        bool OpenPayAtEnd);

    [HttpPost("stations/{stationId}/start-guest")]
    public async Task<IActionResult> StartGuest(string stationId, StartGuestBody body, CancellationToken ct)
    {
        EnsureOperator();
        var session = await sessions.StartGuestAsync(
            stationId,
            body.PrepaidKind,
            body.Minutes,
            body.AmountToman,
            body.OpenPayAtEnd,
            ct);
        return Created($"/api/sessions/{session.Id}", new { session.Id, session.StationId });
    }

    public sealed record CredentialBody(string LoginName, string Password);

    [HttpPut("customers/{customerId:guid}/credential")]
    public async Task<IActionResult> SetCredential(Guid customerId, CredentialBody body, CancellationToken ct)
    {
        EnsureOperator();
        await auth.SetCredentialAsync(customerId, body.LoginName, body.Password, ct);
        return NoContent();
    }

    [HttpGet("games")]
    public async Task<IActionResult> ListGames(CancellationToken ct)
        => Ok((await catalog.GetCatalogAsync(0, ct)));

    public sealed record GameCreateBody(
        string Name,
        int CategoryId,
        string ExecutableOrUri,
        string? Arguments,
        string? IconUrl,
        int? SortOrder,
        bool Enabled = true,
        string[]? GroupRestrictions = null,
        string[]? AllowedProcessNames = null);

    [HttpPost("games")]
    public async Task<IActionResult> CreateGame(GameCreateBody body, CancellationToken ct)
    {
        EnsureManager();
        var row = await catalog.CreateEntryAsync(
            body.Name,
            body.CategoryId,
            body.ExecutableOrUri,
            body.Arguments,
            body.IconUrl,
            body.SortOrder,
            body.Enabled,
            body.GroupRestrictions,
            body.AllowedProcessNames,
            ct);
        return Created($"/api/operator/games/{row.Id}", new { row.Id, row.Name, row.Enabled, row.CatalogVersion });
    }

    public sealed record GameUpdateBody(
        string Name,
        bool Enabled,
        string? ExecutableOrUri,
        string? Arguments,
        string? IconUrl,
        int? SortOrder,
        string[]? GroupRestrictions,
        string[]? AllowedProcessNames);

    [HttpPut("games/{id:int}")]
    public async Task<IActionResult> UpdateGame(int id, GameUpdateBody body, CancellationToken ct)
    {
        EnsureManager();
        var row = await catalog.UpdateEntryAsync(
            id,
            body.Name,
            body.Enabled,
            body.ExecutableOrUri,
            body.Arguments,
            body.IconUrl,
            body.SortOrder,
            body.GroupRestrictions,
            body.AllowedProcessNames,
            ct);
        return Ok(new { row.Id, row.Name, row.Enabled, row.CatalogVersion });
    }

    private void EnsureOperator()
    {
        if (!op.IsAuthenticated)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "auth", "Operator login required.");
        }
    }

    private void EnsureManager()
    {
        if (!op.IsAuthenticated)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "auth", "Operator login required.");
        }

        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager permission required.");
        }
    }
}
