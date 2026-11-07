using GameClub.Server.Operator;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/shift")]
public sealed class OperatorShiftController(OperatorShiftCloseService shifts) : ControllerBase
{
    [HttpGet("state")]
    public Task<IActionResult> State(CancellationToken ct) => OkTask(shifts.GetLiveStateAsync(ct));

    public sealed record SettingsRequest(bool? ShiftsEnabled, bool? MultiOperatorEnabled);

    [HttpPut("settings")]
    public Task<IActionResult> Settings(SettingsRequest body, CancellationToken ct)
        => OkTask(shifts.UpdateSettingsAsync(body.ShiftsEnabled, body.MultiOperatorEnabled, ct));

    public sealed record OpenShiftRequest(long OpeningFloatToman);

    [HttpPost("open")]
    public Task<IActionResult> Open(OpenShiftRequest body, CancellationToken ct)
        => OkTask(shifts.OpenShiftAsync(body.OpeningFloatToman, ct));

    [HttpGet("close/preview")]
    public Task<IActionResult> Preview(CancellationToken ct) => OkTask(shifts.PreviewCloseAsync(ct));

    public sealed record CloseRequest(
        WorkingDayInput WorkingDay,
        long ActualCashToman,
        long ActualCardReaderToman,
        string? DiscrepancyExplanation,
        IReadOnlyList<CloseSessionAction>? SessionActions,
        Guid? NextRecipientUserId);

    [HttpPost("close")]
    public Task<IActionResult> Close(CloseRequest body, CancellationToken ct)
        => OkTask(shifts.CloseAsync(
            body.WorkingDay,
            body.ActualCashToman,
            body.ActualCardReaderToman,
            body.DiscrepancyExplanation,
            body.SessionActions ?? [],
            body.NextRecipientUserId,
            ct));

    private async Task<IActionResult> OkTask(Task<object> task)
    {
        return Ok(await task);
    }
}

[ApiController]
[Route("api/operator/reports")]
public sealed class OperatorReportsController(OperatorReportingService reports) : ControllerBase
{
    [HttpGet("income/daily")]
    public Task<IActionResult> Daily(
        [FromQuery] int year,
        [FromQuery] int month,
        [FromQuery] int day,
        CancellationToken ct)
        => OkTask(reports.DailyIncomeAsync(year, month, day, ct));

    [HttpGet("history")]
    public Task<IActionResult> History(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? stationId,
        [FromQuery] Guid? customerId,
        [FromQuery] Guid? operatorId,
        [FromQuery] string? method,
        CancellationToken ct)
        => OkTask(reports.HistoryAsync(fromUtc, toUtc, stationId, customerId, operatorId, method, ct));

    [HttpGet("history/export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? stationId,
        [FromQuery] Guid? customerId,
        [FromQuery] Guid? operatorId,
        [FromQuery] string? method,
        CancellationToken ct)
    {
        var bytes = await reports.HistoryExcelAsync(fromUtc, toUtc, stationId, customerId, operatorId, method, ct);
        return File(bytes, "text/csv", "history.csv");
    }

    [HttpGet("usage")]
    public Task<IActionResult> Usage([FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc, CancellationToken ct)
        => OkTask(reports.UsageAsync(fromUtc, toUtc, ct));

    [HttpGet("wallet/cu8")]
    public Task<IActionResult> Cu8(CancellationToken ct) => OkTask(reports.Cu8WalletReportsAsync(ct));

    [HttpGet("close/{closeRecordId:guid}/income")]
    public Task<IActionResult> CloseIncome(Guid closeRecordId, CancellationToken ct)
        => OkTask(reports.CloseDayIncomeAsync(closeRecordId, ct));

    private async Task<IActionResult> OkTask(Task<object> task) => Ok(await task);
}
