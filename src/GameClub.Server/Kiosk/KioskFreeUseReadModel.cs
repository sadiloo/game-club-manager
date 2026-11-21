using GameClub.Kiosk.Core.Admin;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Kiosk;

internal static class KioskFreeUseReadModel
{
    public static OpenFreeUseView ForOpenRow(KioskFreeUseEntity row, DateTime? deviceLastContactUtc, DateTime nowUtc)
    {
        var lastContact = deviceLastContactUtc;
        var noContact = KioskFreeUsePresence.IsNoContact(lastContact, nowUtc, isOpen: true);
        return new OpenFreeUseView(row.Id, row.StationId, row.StartedAtUtc, row.ActorRole, lastContact, noContact);
    }

    public static ReportRowView ForReportRow(
        KioskFreeUseEntity row,
        DateTime? deviceLastContactUtc,
        DateTime nowUtc)
    {
        var isOpen = row.EndedAtUtc is null;
        var lastContact = isOpen ? deviceLastContactUtc : null;
        var noContact = KioskFreeUsePresence.IsNoContact(lastContact, nowUtc, isOpen);
        var durationEnd = KioskFreeUsePresence.EffectiveDurationEnd(row.EndedAtUtc, lastContact, nowUtc, noContact);
        var durationSeconds = (long)Math.Max(0, (durationEnd - row.StartedAtUtc).TotalSeconds);
        return new ReportRowView(
            row.StationId,
            row.ActorRole,
            row.StartedAtUtc,
            row.EndedAtUtc,
            row.EndReason,
            lastContact,
            noContact,
            durationSeconds);
    }

    internal readonly record struct OpenFreeUseView(
        Guid Id,
        string StationId,
        DateTime StartedAtUtc,
        string ActorRole,
        DateTime? LastContactUtc,
        bool NoContact);

    internal readonly record struct ReportRowView(
        string StationId,
        string ActorRole,
        DateTime StartedAtUtc,
        DateTime? EndedAtUtc,
        string? EndReason,
        DateTime? LastContactUtc,
        bool NoContact,
        long DurationSeconds);
}