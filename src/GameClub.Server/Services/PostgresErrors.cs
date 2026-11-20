using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameClub.Server.Services;

public static class PostgresErrors
{
    public const string ActiveSessionIndexName = "ux_sessions_station_active";

    public const string OpenPauseIndexName = "ux_pause_intervals_session_open";

    public const string SessionCommandIndexName = "IX_session_commands_SessionId_CommandId";

    public const string WalletCommandIndexName = "UX_wallet_commands_CommandId";

    public const string WalletTopUpCorrectionCommandIndexName = "UX_wallet_topup_corrections_CommandId";

    public static bool IsSessionCommandUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, SessionCommandIndexName);

    public static bool IsWalletCommandUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, WalletCommandIndexName);

    public static bool IsWalletTopUpCorrectionCommandUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, WalletTopUpCorrectionCommandIndexName);

    public static bool IsActiveSessionUniqueViolation(DbUpdateException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg
                && pg.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(pg.ConstraintName, ActiveSessionIndexName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsOpenPauseUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, OpenPauseIndexName);

    public static bool IsInvoiceSessionUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, "IX_invoices_SessionId");

    public static bool IsStationIdUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, "PK_stations") || IsUniqueViolation(ex, "IX_stations_Id");

    public static bool IsCustomerMobileUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, "IX_customers_Mobile");

    public static bool IsCustomerMembershipCodeUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, "IX_customers_MembershipCodeKey");

    private static bool IsUniqueViolation(DbUpdateException ex, string constraintName)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg
                && pg.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCloseRecordShiftUniqueViolation(DbUpdateException ex)
        => IsUniqueViolation(ex, "IX_close_records_ShiftId");

    public static bool IsLockNotAvailable(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDeadlock(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSerializationFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsActiveSessionStationBusyConflict(DbUpdateException ex)
        => IsActiveSessionUniqueViolation(ex) || IsSerializationFailure(ex) || IsDeadlock(ex);
}
