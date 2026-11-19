using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Observability;

internal static class ObservabilityLogFormats
{
    internal static long UtcMs() =>
        (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

    internal static string LeaseKeyHash8(SignedSessionLease? lease)
    {
        if (lease is null || string.IsNullOrEmpty(lease.SignatureBase64))
        {
            return "";
        }

        byte[] bytes;
        using (var sha = SHA256.Create())
        {
            bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(lease.SignatureBase64));
        }
#if NET10_0_OR_GREATER
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
#else
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
#endif
    }

    internal static void WriteGuardPipeStep(string verb, string evt, long? durationMs = null, string? errorCode = null)
    {
        var line = $"GUARD_PIPE utc_ms={UtcMs()} verb={verb} event={evt}";
        if (durationMs is not null)
        {
            line += $" duration_ms={durationMs.Value}";
        }

        if (errorCode is not null)
        {
            line += $" error_code={errorCode}";
        }

        Console.WriteLine(line);
        Console.Out.Flush();
    }

    internal static void WriteGuardPipeSubStep(string verb, string evt)
    {
        var line =
            $"GUARD_PIPE utc_ms={UtcMs()} verb={verb} event={evt} thread={Thread.CurrentThread.ManagedThreadId}";
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    internal static (string VerifyResult, string? LockedReason) DescribeLeaseAndLock(
        KioskHeartbeatAck ack,
        Guid hostSessionId,
        KioskShellPhase phase,
        bool leaseVerifyOk,
        string leaseVerifyDetail)
    {
        string? lockedReason = null;
        if (phase == KioskShellPhase.Locked)
        {
            if (ack.ManualSettle
                || string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                lockedReason = "other:ended_path";
            }
            else if (!string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
            {
                lockedReason = "status_not_open";
            }
            else if (hostSessionId == Guid.Empty)
            {
                lockedReason = "other:empty_host_session";
            }
            else if (ack.Lease is null)
            {
                lockedReason = "no_lease";
            }
            else if (ack.Lease.Payload.SessionId is Guid leaseSession
                     && leaseSession != Guid.Empty
                     && leaseSession != hostSessionId)
            {
                lockedReason = "lease_session_mismatch";
            }
            else if (!leaseVerifyOk)
            {
                lockedReason = "lease_verify_failed";
            }
            else
            {
                lockedReason = "other:locked_after_open";
            }
        }

        var verify = leaseVerifyOk ? "ok" : $"fail:{leaseVerifyDetail}";
        return (verify, lockedReason);
    }

    internal static void WriteHbAck(
        KioskHeartbeatAck ack,
        Guid hostSessionId,
        Guid runtimeSessionId,
        KioskShellPhase phase,
        string leaseVerifyResult,
        string? lockedReason)
    {
        var ackLeaseSession = ack.Lease?.Payload.SessionId ?? Guid.Empty;
        var line =
            "HB_ACK "
            + $"utc_ms={UtcMs()} "
            + $"ackSessionStatus={ack.SessionStatus} "
            + $"ackLeaseSessionId={ackLeaseSession} "
            + $"hostSessionId={hostSessionId} "
            + $"runtimeSessionId={runtimeSessionId} "
            + $"leaseVerify={leaseVerifyResult} "
            + $"leaseSessionId={ackLeaseSession} "
            + $"leaseKeyHash8={LeaseKeyHash8(ack.Lease)} "
            + $"shellPhase={phase}";
        if (phase == KioskShellPhase.Locked && lockedReason is not null)
        {
            line += $" lockedReason={lockedReason}";
        }

        Console.WriteLine(line);
        Console.Out.Flush();
    }
}