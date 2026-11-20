using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Runtime;

public enum KioskRuntimeState
{
    Unpaired,
    Locked,
    Playing,
    ManualSettle,
    Ended,
}

public sealed class KioskRuntime : IAsyncDisposable
{
    private readonly Func<HttpClient> _httpFactory;
    private IKioskHubTransport? _transport;
    private HubConnection? _hub;
    private IOfflineEventQueue? _queue;
    private CancellationTokenSource? _heartbeatCts;
    private CancellationTokenSource? _lifecycleCts;
    private KioskHubLifecycle? _lifecycle;
    private Task? _heartbeatSupervisor;
    private Guid _sessionId;
    private readonly Stopwatch _monotonic = new();
    private bool _monotonicArmedForStart;
    private bool _monotonicRestartedForInstance;
    private bool _disposed;
    private int _consecutiveHeartbeatFailures;
    private readonly GuardInternetClient _guardInternet = new();
    private readonly object _internetApplyGate = new();
    private readonly Queue<SignedInternetCommand> _internetApplyQueue = new();
    private (Guid Epoch, long Version)? _internetApplyInFlightKey;
    private CancellationTokenSource? _internetApplyCts;
    private Task? _internetApplyWorker;
    private int _internetApplyInvocationCount;
    private KioskInternetAppliedReport? _lastInternetReport;
    private bool _internetOffApplied;

    public KioskRuntimeState State { get; private set; } = KioskRuntimeState.Unpaired;
    public bool InternetOffBadgeVisible => _internetOffApplied;
    public string ServerBase { get; private set; } = "";
    public string StationId { get; private set; } = "";
    public Guid DeviceId { get; private set; }
    public string DeviceKey { get; private set; } = "";

    public Guid BoundSessionId => _sessionId;

    public event Action<KioskHeartbeatAck>? HeartbeatAcknowledged;

    public event Action? HeartbeatTransportFailed;

    public event Action<KioskTransportPhase, string>? ConnectionStatusChanged;

    public long ClientMonotonicMs => _monotonic.ElapsedMilliseconds;

    public bool IsTransportConnected =>
        _lifecycle?.IsTransportConnected == true || _transport?.State == HubConnectionState.Connected;

    public KioskTransportPhase TransportPhase =>
        _lifecycle?.CurrentPhase ?? KioskTransportPhase.Unconfigured;

    public bool SignalRAutoReconnecting => _transport?.SignalRAutoReconnecting == true;

    public int ConsecutiveHeartbeatFailures => _consecutiveHeartbeatFailures;

    public DateTime? LastSuccessfulHeartbeatAckUtc { get; private set; }

    public string LastTransportErrorDetail { get; private set; } = "";

    private long _lastTransportFailedSignalMs;

    internal Func<TimeSpan, CancellationToken, Task>? TestDelayAsync { get; set; }

    internal Func<int, TimeSpan>? TestBackoffPolicy { get; set; }

    internal bool TestSingleHeartbeatIteration { get; set; }

    internal bool TestDisableHeartbeatSupervisorRestart { get; set; }

    internal int HeartbeatLoopStartCount { get; private set; }

    internal TimeSpan TestHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal Func<SignedInternetCommand, CancellationToken, Task<string>>? TestGuardInternetApplyAsync { get; set; }

    internal KioskInternetAppliedReport? LastInternetReportForTests => Volatile.Read(ref _lastInternetReport);

    internal int InternetApplyInvocationCountForTests => Volatile.Read(ref _internetApplyInvocationCount);

    public KioskRuntime(Func<HttpClient> httpFactory) => _httpFactory = httpFactory;

    public void Configure(
        Uri hubUri,
        Guid deviceId,
        string deviceKey,
        string stationId,
        string queuePath,
        bool legacyWindows,
        HttpMessageHandler? testHubHandler = null)
    {
        ServerBase = hubUri.GetLeftPart(UriPartial.Authority);
        DeviceId = deviceId;
        DeviceKey = deviceKey;
        StationId = stationId;
        if (_queue is null)
        {
            _queue = new SqliteOfflineEventQueue(queuePath);
        }

        _hub = KioskConnectionFactory.Create(hubUri, deviceId.ToString(), deviceKey, legacyWindows, testHubHandler);
        _transport = new SignalRHubTransport(_hub);
        WireLifecycle();
        State = KioskRuntimeState.Locked;
    }

    internal async Task ResetForReconfigureAsync()
    {
        if (_disposed)
        {
            return;
        }

        _lifecycleCts?.Cancel();
        _lifecycle?.ConnectionStatusChanged -= ForwardConnectionStatus;
        if (_lifecycle is not null)
        {
            await _lifecycle.DisposeAsync().ConfigureAwait(false);
            _lifecycle = null;
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
            _hub = null;
        }
    }

    internal void ConfigureForTests(
        IKioskHubTransport transport,
        IOfflineEventQueue queue,
        string serverBase,
        Guid deviceId,
        string deviceKey,
        string stationId)
    {
        ServerBase = serverBase;
        DeviceId = deviceId;
        DeviceKey = deviceKey;
        StationId = stationId;
        _queue = queue;
        _transport = transport;
        WireLifecycle();
        State = KioskRuntimeState.Locked;
    }

    private void WireLifecycle()
    {
        if (_transport is null)
        {
            return;
        }

        _lifecycle?.ConnectionStatusChanged -= ForwardConnectionStatus;
        _lifecycle = new KioskHubLifecycle(_transport)
        {
            DelayAsync = TestDelayAsync,
            BackoffPolicy = TestBackoffPolicy,
        };
        _lifecycle.ConnectionStatusChanged += ForwardConnectionStatus;
    }

    private void ForwardConnectionStatus(KioskTransportPhase phase, string detail)
    {
        if (phase == KioskTransportPhase.Connected && _monotonicArmedForStart && !_monotonicRestartedForInstance)
        {
            _monotonic.Restart();
            _monotonicArmedForStart = false;
            _monotonicRestartedForInstance = true;
        }

        if (phase == KioskTransportPhase.LastError)
        {
            LastTransportErrorDetail = detail;
        }

        ConnectionStatusChanged?.Invoke(phase, detail);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_transport is null || _queue is null || _lifecycle is null)
        {
            throw new InvalidOperationException("Kiosk runtime is not configured.");
        }

        if (!_monotonicRestartedForInstance)
        {
            _monotonicArmedForStart = true;
        }

        _lifecycleCts?.Cancel();
        _lifecycleCts?.Dispose();
        _lifecycleCts = new CancellationTokenSource();
        await _lifecycle.BeginAsync(_lifecycleCts.Token).ConfigureAwait(false);
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = new CancellationTokenSource();
        _internetApplyCts?.Cancel();
        _internetApplyCts?.Dispose();
        _internetApplyCts = null;
        lock (_internetApplyGate)
        {
            _internetApplyQueue.Clear();
            _internetApplyInFlightKey = null;
            _internetApplyWorker = null;
        }

        _heartbeatSupervisor = RunHeartbeatSupervisorAsync(_heartbeatCts.Token);
        State = _sessionId == Guid.Empty ? KioskRuntimeState.Locked : KioskRuntimeState.Playing;
    }

    public Task WaitForTransportConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_lifecycle is null)
        {
            throw new InvalidOperationException("Kiosk runtime is not configured.");
        }

        return _lifecycle.WaitForTransportConnectedAsync(timeout, ct);
    }

    public void BindSession(Guid sessionId)
    {
        _sessionId = sessionId;
        if (IsTransportConnected && sessionId != Guid.Empty)
        {
            State = KioskRuntimeState.Playing;
        }
        else if (sessionId == Guid.Empty)
        {
            State = KioskRuntimeState.Locked;
        }
    }

    public IOfflineEventQueue GetQueue()
    {
        if (_queue is null)
        {
            throw new InvalidOperationException("Kiosk runtime is not configured.");
        }

        return _queue;
    }

    private async Task RunHeartbeatSupervisorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HeartbeatLoopStartCount++;
            var loop = RunHeartbeatAsync(ct);
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (TestDisableHeartbeatSupervisorRestart)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_transport?.State == HubConnectionState.Connected)
                {
                    var ack = await _transport.InvokeAsync<KioskHeartbeatAck>(
                        "Heartbeat",
                        new KioskHeartbeatRequest
                        {
                            SessionId = _sessionId == Guid.Empty ? null : _sessionId,
                            ClientMonotonicMs = _monotonic.ElapsedMilliseconds,
                            InternetApplied = Volatile.Read(ref _lastInternetReport),
                        },
                        ct);
                    OfferInternetCommand(ack.Internet);
                    ApplyAcknowledgement(ack);
                    _consecutiveHeartbeatFailures = 0;
                    LastSuccessfulHeartbeatAckUtc = DateTime.UtcNow;
                    HeartbeatAcknowledged?.Invoke(ack);
                }
                else
                {
                    _consecutiveHeartbeatFailures++;
                    SignalTransportDownIfNeeded();
                }
            }
            catch (Exception ex)
            {
                _consecutiveHeartbeatFailures++;
                try
                {
                    await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);
                }
                catch
                {
                }

                SignalTransportDownIfNeeded();
            }

            if (TestSingleHeartbeatIteration)
            {
                return;
            }

            await Task.Delay(TestHeartbeatInterval, ct);
        }
    }

    public void SignalTransportDownIfNeeded()
    {
        if (_sessionId == Guid.Empty || IsTransportConnected)
        {
            return;
        }

        var ms = _monotonic.ElapsedMilliseconds;
        if (_lastTransportFailedSignalMs > 0 && ms - _lastTransportFailedSignalMs < 30_000)
        {
            return;
        }

        HeartbeatTransportFailed?.Invoke();
        _lastTransportFailedSignalMs = ms;
    }

    private void OfferInternetCommand(SignedInternetCommand? command)
    {
        if (command is null || _heartbeatCts is null)
        {
            return;
        }

        var key = (command.Payload.Epoch, command.Payload.Version);
        lock (_internetApplyGate)
        {
            var inflight = _internetApplyInFlightKey;
            if (inflight == key)
            {
                return;
            }

            var prior = Volatile.Read(ref _lastInternetReport);
            if (prior is not null
                && prior.Epoch == key.Epoch
                && prior.Version == key.Version
                && string.IsNullOrEmpty(prior.Error))
            {
                return;
            }

            foreach (var queued in _internetApplyQueue)
            {
                if (queued.Payload.Epoch == key.Epoch && queued.Payload.Version == key.Version)
                {
                    return;
                }
            }

            _internetApplyQueue.Enqueue(command);
            EnsureInternetApplyWorkerLocked(_heartbeatCts.Token);
        }
    }

    private void EnsureInternetApplyWorkerLocked(CancellationToken heartbeatCt)
    {
        if (_internetApplyWorker is { IsCompleted: false })
        {
            return;
        }

        _internetApplyCts?.Cancel();
        _internetApplyCts?.Dispose();
        _internetApplyCts = CancellationTokenSource.CreateLinkedTokenSource(heartbeatCt);
        _internetApplyWorker = RunInternetApplyWorkerAsync(_internetApplyCts.Token);
    }

    private async Task RunInternetApplyWorkerAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SignedInternetCommand? command;
                lock (_internetApplyGate)
                {
                    if (_internetApplyQueue.Count == 0)
                    {
                        break;
                    }

                    command = _internetApplyQueue.Dequeue();
                    _internetApplyInFlightKey = (command.Payload.Epoch, command.Payload.Version);
                }

                try
                {
                    await ApplyInternetCommandAsync(command, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_internetApplyGate)
                    {
                        _internetApplyInFlightKey = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_internetApplyGate)
            {
                _internetApplyWorker = null;
            }
        }
    }

    private async Task ApplyInternetCommandAsync(SignedInternetCommand command, CancellationToken ct)
    {
        Interlocked.Increment(ref _internetApplyInvocationCount);
        try
        {
            var raw = TestGuardInternetApplyAsync is not null
                ? await TestGuardInternetApplyAsync(command, ct).ConfigureAwait(false)
                : await _guardInternet.ApplyAsync(command, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                PublishInternetReport(new KioskInternetAppliedReport
                {
                    Epoch = command.Payload.Epoch,
                    Version = command.Payload.Version,
                    On = command.Payload.On,
                    AppliedAtUtc = DateTime.UtcNow,
                    Error = err.GetString(),
                });
                return;
            }

            var on = root.TryGetProperty("on", out var onEl) && onEl.GetBoolean();
            _internetOffApplied = !on;
            PublishInternetReport(new KioskInternetAppliedReport
            {
                Epoch = command.Payload.Epoch,
                Version = command.Payload.Version,
                On = on,
                AppliedAtUtc = root.TryGetProperty("appliedAtUtc", out var at) && at.ValueKind == JsonValueKind.String
                    ? DateTime.Parse(at.GetString()!).ToUniversalTime()
                    : DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            var code = GuardInternetClientErrorCodes.MapException(ex);
            PublishInternetReport(new KioskInternetAppliedReport
            {
                Epoch = command.Payload.Epoch,
                Version = command.Payload.Version,
                On = command.Payload.On,
                AppliedAtUtc = DateTime.UtcNow,
                Error = code,
            });
        }
    }

    private void PublishInternetReport(KioskInternetAppliedReport report)
        => Volatile.Write(ref _lastInternetReport, report);

    private void ApplyAcknowledgement(KioskHeartbeatAck ack)
    {
        if (ack.ManualSettle)
        {
            State = KioskRuntimeState.ManualSettle;
            return;
        }

        if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
        {
            if (ack.Lease?.Payload.SessionId is Guid leased && leased != Guid.Empty)
            {
                if (_sessionId != Guid.Empty && leased != _sessionId)
                {
                    State = KioskRuntimeState.Locked;
                    return;
                }

                if (_sessionId == Guid.Empty)
                {
                    _sessionId = leased;
                }
            }

            if (_sessionId != Guid.Empty)
            {
                State = KioskRuntimeState.Playing;
            }

            return;
        }

        if (string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase)
            || (_sessionId != Guid.Empty && !string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(ack.SessionStatus) && ack.SessionStatus != "Paused"))
        {
            State = KioskRuntimeState.Ended;
            return;
        }

        if (_sessionId == Guid.Empty)
        {
            State = KioskRuntimeState.Locked;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifecycleCts?.Cancel();
        _heartbeatCts?.Cancel();
        _internetApplyCts?.Cancel();
        if (_internetApplyWorker is not null)
        {
            try
            {
                await _internetApplyWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_heartbeatSupervisor is not null)
        {
            try
            {
                await _heartbeatSupervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_lifecycle is not null)
        {
            _lifecycle.ConnectionStatusChanged -= ForwardConnectionStatus;
            await _lifecycle.DisposeAsync();
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        _internetApplyCts?.Dispose();
        _heartbeatCts?.Dispose();
        _lifecycleCts?.Dispose();
    }
}