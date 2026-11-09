using System.Diagnostics;
using System.Text.Json;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Runtime;

public enum KioskShellPhase
{
    Locked,
    Playing,
    LeaseExpired,
    Ended,
}

public sealed class KioskShellOrchestrator
{
    private LeaseVerifier? _leaseVerifier;
    private IOfflineEventQueue? _queue;
    private SignedSessionLease? _lastLease;
    private Guid _boundSessionId;
    private long _lastReplayMonotonicMs;
    private long _lastSegmentMonotonicMs;
    private readonly ProcessPolicyEnforcer _policyEnforcer = new();
    private readonly List<int> _trackedLaunchPids = [];

    public KioskShellPhase Phase { get; private set; } = KioskShellPhase.Locked;
    public bool AdminUrlUnlocked { get; private set; }
    public bool FinalMode { get; private set; } = true;
    public string StationGroupId { get; set; } = "pc-normal";

    public void ConfigureLeaseVerifier(string publicKeyPem)
    {
        _leaseVerifier = new LeaseVerifier(publicKeyPem);
    }

    public void BindOfflineQueue(IOfflineEventQueue queue) => _queue = queue;

    public void SetFinalMode(bool finalMode) => FinalMode = finalMode;

    public bool TryUnlockAdmin(string? configuredPassword, string attempt)
    {
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            return false;
        }

        AdminUrlUnlocked = string.Equals(configuredPassword, attempt, StringComparison.Ordinal);
        return AdminUrlUnlocked;
    }

    public bool TrySafeTestExit(string? configuredPassword, string attempt)
        => !FinalMode && TryUnlockAdmin(configuredPassword, attempt);

    public KioskShellPhase OnHeartbeatAck(KioskHeartbeatAck ack, Guid sessionId, long clientMonotonicMs)
    {
        if (ack.ManualSettle
            || string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase))
        {
            Phase = KioskShellPhase.Ended;
            return Phase;
        }

        if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase) && sessionId != Guid.Empty)
        {
            if (ack.Lease?.Payload.SessionId is Guid leaseSession
                && leaseSession != Guid.Empty
                && leaseSession != sessionId)
            {
                Phase = KioskShellPhase.Locked;
                return Phase;
            }

            if (!VerifyLease(ack.Lease))
            {
                Phase = KioskShellPhase.LeaseExpired;
                return Phase;
            }

            _boundSessionId = sessionId;
            _lastLease = ack.Lease;
            Phase = KioskShellPhase.Playing;
            EnqueuePoweredSegment(sessionId, clientMonotonicMs);
            return Phase;
        }

        Phase = KioskShellPhase.Locked;
        return Phase;
    }

    public KioskShellPhase TickLease(Guid sessionId, long clientMonotonicMs, DateTime nowUtc)
    {
        if (Phase != KioskShellPhase.Playing || _lastLease is null)
        {
            return Phase;
        }

        if (sessionId != Guid.Empty && _boundSessionId != Guid.Empty && sessionId != _boundSessionId)
        {
            Phase = KioskShellPhase.Locked;
            return Phase;
        }

        if (_lastLease.Payload.AuthorizedUntilUtc < nowUtc)
        {
            Phase = KioskShellPhase.LeaseExpired;
            return Phase;
        }

        return Phase;
    }

    public void EnqueuePoweredSegmentOnDisconnect(Guid sessionId, long clientMonotonicMs)
        => EnqueuePoweredSegment(sessionId, clientMonotonicMs);

    public void TrackLaunchedProcess(int processId)
    {
        if (!_trackedLaunchPids.Contains(processId))
        {
            _trackedLaunchPids.Add(processId);
        }
    }

    public bool VerifyLease(SignedSessionLease? lease)
    {
        if (_leaseVerifier is null || lease is null)
        {
            return false;
        }

        if (!_leaseVerifier.TryVerify(lease))
        {
            return false;
        }

        if (lease.Payload.AuthorizedUntilUtc < DateTime.UtcNow)
        {
            return false;
        }

        return true;
    }

    public IReadOnlyList<GameCatalogItem> FilterVisibleGames(IEnumerable<GameCatalogItem> catalog)
        => GameCatalogFilter.ForStation(catalog, StationGroupId).ToList();

    public void EnforceAllowlist(IReadOnlyList<string> mergedAllowlist)
        => _policyEnforcer.EnforceAllowlist(mergedAllowlist);

    public async Task<int> DrainOfflineQueueAsync(
        Func<IReadOnlyList<KioskOutboundEvent>, Task<IReadOnlyList<Guid>>> submitAsync,
        CancellationToken ct)
    {
        if (_queue is null)
        {
            return 0;
        }

        var pending = await _queue.PendingAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var batch = pending.Select(p => new KioskOutboundEvent
        {
            EventId = p.EventId,
            Type = p.Type,
            PayloadJson = p.PayloadJson,
            CreatedUtc = p.CreatedUtc,
        }).ToList();
        var acked = await submitAsync(batch);
        foreach (var id in acked)
        {
            await _queue.AckAsync(id, ct);
        }

        return acked.Count;
    }

    public void EndSessionCleanup()
    {
        foreach (var pid in _trackedLaunchPids.ToArray())
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch
            {
                // process already gone
            }
        }

        _trackedLaunchPids.Clear();
        Phase = KioskShellPhase.Locked;
        _lastLease = null;
        _boundSessionId = Guid.Empty;
        _lastSegmentMonotonicMs = 0;
        _lastReplayMonotonicMs = 0;
        AdminUrlUnlocked = false;
    }

    public void KillDeniedProcesses() => _policyEnforcer.EnforceDenyList(ProcessPolicyEnforcer.FreezeDenyProcessNames);

    private void EnqueuePoweredSegment(Guid sessionId, long monotonicMs)
    {
        if (_queue is null || sessionId == Guid.Empty || monotonicMs <= _lastSegmentMonotonicMs)
        {
            return;
        }

        if (monotonicMs - _lastReplayMonotonicMs < 30_000 && _lastSegmentMonotonicMs > 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            sessionId,
            powered = true,
            segmentEndUtc = DateTime.UtcNow,
            clientMonotonicMs = monotonicMs,
        });
        _queue.EnqueueAsync(
            Guid.NewGuid(),
            "offline_play_segment",
            payload,
            DateTime.UtcNow).GetAwaiter().GetResult();
        _lastSegmentMonotonicMs = monotonicMs;
    }
}
