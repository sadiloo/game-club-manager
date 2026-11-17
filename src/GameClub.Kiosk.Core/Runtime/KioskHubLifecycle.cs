using System.Threading;
using GameClub.Kiosk.Core.Connection;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Runtime;

internal sealed class KioskHubLifecycle : IAsyncDisposable
{
    private readonly IKioskHubTransport _transport;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _lifecycleCts;
    private Task? _loopTask;
    private int _attempt;
    private readonly object _closedLock = new();
    private TaskCompletionSource<bool>? _closedSignal;

    public Func<int, TimeSpan>? BackoffPolicy { get; set; }

    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; set; }

    public event Action<KioskTransportPhase, string>? ConnectionStatusChanged;

    public KioskHubLifecycle(IKioskHubTransport transport)
    {
        _transport = transport;
        _transport.Closed += OnTransportClosedAsync;
    }

    public bool IsTransportConnected => _transport.State == HubConnectionState.Connected;

    public KioskTransportPhase CurrentPhase { get; private set; } = KioskTransportPhase.Unconfigured;

    public void Begin(CancellationToken parentCancellation)
    {
        if (_loopTask is not null)
        {
            return;
        }

        _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        _loopTask = RunReconnectLoopAsync(_lifecycleCts.Token);
    }

    public async Task WaitForTransportConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTransportConnected)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("Kiosk transport did not connect before timeout.");
    }

    private Task OnTransportClosedAsync(Exception? _)
    {
        TaskCompletionSource<bool>? signal;
        lock (_closedLock)
        {
            signal = _closedSignal;
        }

        signal?.TrySetResult(true);
        return Task.CompletedTask;
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _connectGate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                Publish(KioskTransportPhase.Connecting, "");
                TaskCompletionSource<bool> closedWait;
                lock (_closedLock)
                {
                    closedWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _closedSignal = closedWait;
                }

                try
                {
                    await _transport.StartAsync(ct);
                    Interlocked.Exchange(ref _attempt, 0);
                    Publish(KioskTransportPhase.Connected, "");

                    using (ct.Register(() => closedWait.TrySetCanceled()))
                    {
                        try
                        {
                            await closedWait.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Publish(KioskTransportPhase.LastError, ex.Message);
                }
                finally
                {
                    lock (_closedLock)
                    {
                        _closedSignal = null;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (_transport.State != HubConnectionState.Connected)
                {
                    Publish(KioskTransportPhase.Disconnected, "");
                }

                var attempt = Interlocked.Increment(ref _attempt) - 1;
                var delay = (BackoffPolicy ?? KioskReconnectBackoff.DelayForAttempt)(attempt);
                Publish(KioskTransportPhase.Reconnecting, "");
                var delayFunc = DelayAsync ?? DefaultDelayAsync;
                await delayFunc(delay, ct);
            }
            finally
            {
                _connectGate.Release();
            }
        }
    }

    private static Task DefaultDelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    private void Publish(KioskTransportPhase phase, string detail)
    {
        CurrentPhase = phase;
        ConnectionStatusChanged?.Invoke(phase, detail);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.Closed -= OnTransportClosedAsync;
        if (_lifecycleCts is not null)
        {
            _lifecycleCts.Cancel();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifecycleCts?.Dispose();
        _connectGate.Dispose();
    }
}