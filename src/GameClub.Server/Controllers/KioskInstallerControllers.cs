using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Setup;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/kiosk/setup")]
public sealed class KioskSetupController(
    KioskCredentialService credentials,
    KioskSetupLockTracker setupLock,
    IClock clock,
    IOptions<InstallServerOptions> installOptions) : ControllerBase
{
    [HttpGet("hello")]
    public IActionResult Hello()
    {
        var serverId = installOptions.Value.ServerId;
        if (string.IsNullOrWhiteSpace(serverId))
        {
            serverId = "dev";
        }

        return Ok(new { product = "Checkpoint", serverId });
    }

    public sealed record VerifyAdminPasswordRequest(string Password);

    [HttpPost("verify-admin-password")]
    public async Task<IActionResult> VerifyAdminPassword(VerifyAdminPasswordRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var utcNow = clock.UtcNow.UtcDateTime;
        setupLock.ThrowIfLocked(ip, utcNow);

        var hashes = await credentials.TryGetHashesAsync(ct);
        if (hashes is null)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new { code = "kiosk_admin_password_not_set", message = "رمز مدیر کیوسک هنوز در برنامهٔ سرور ساخته نشده است." });
        }

        if (string.IsNullOrEmpty(body.Password))
        {
            setupLock.RegisterFailure(ip, utcNow);
            return Unauthorized(new { code = "invalid_password", message = "رمز اشتباه است." });
        }

        if (!await credentials.VerifyManagerPasswordAsync(body.Password, ct))
        {
            setupLock.RegisterFailure(ip, utcNow);
            if (setupLock.IsLocked(ip, utcNow, out var until) && until is not null)
            {
                setupLock.ThrowIfLocked(ip, utcNow);
            }

            return Unauthorized(new { code = "invalid_password", message = "رمز اشتباه است." });
        }

        setupLock.RegisterSuccess(ip);
        return Ok(new { kioskManagerHash = hashes.Value.ManagerHash, kioskOperatorHash = hashes.Value.OperatorHash });
    }
}

[ApiController]
[Route("api/operator/kiosk-credentials")]
public sealed class KioskCredentialsController(KioskCredentialService credentials, OperatorContext op) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (op.Role is UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "فقط مدیر یا مالک مجاز است.");
        }

        if (op.Role is not (UserRole.Manager or UserRole.Owner))
        {
            return Unauthorized();
        }

        var status = await credentials.GetStatusAsync(ct);
        return Ok(new
        {
            managerSet = status.ManagerSet,
            operatorSet = status.OperatorSet,
            managerUpdatedAtUtc = status.ManagerUpdatedAtUtc,
            operatorUpdatedAtUtc = status.OperatorUpdatedAtUtc,
        });
    }

    public sealed record PutCredentialRequest(string NewPassword);

    [HttpPut("{role}")]
    public async Task<IActionResult> Put(string role, PutCredentialRequest body, CancellationToken ct)
    {
        if (op.Role is UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "فقط مدیر یا مالک مجاز است.");
        }

        if (op.Role is not (UserRole.Manager or UserRole.Owner))
        {
            return Unauthorized();
        }

        await credentials.SetCredentialAsync(role, body.NewPassword, ct);
        return NoContent();
    }
}