#if NET48
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

internal sealed class ShellPanelHttpSplitStub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _inflightHandlers = new();
    private readonly object _handlerLock = new();
    private readonly object _telemetryLock = new();
    private TaskCompletionSource<bool>? _releaseHeldOperatorResponse;
    private int _holdNextOperatorResponse;
    private readonly List<DateTime> _probeSuccessUtcTimestamps = new();
    private readonly List<OperatorDelayWindow> _operatorDelayWindows = new();
    private Task? _listenTask;
    private readonly ManualResetEventSlim _started = new(false);

    private sealed class OperatorDelayWindow
    {
        public DateTime StartUtc { get; init; }
        public DateTime? EndUtc { get; set; }
        public int DelayMilliseconds { get; init; }
    }

    public ShellPanelHttpSplitStub(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public int OperatorStatusCode { get; set; } = 503;

    public int OperatorDelayMilliseconds { get; set; }

    public int ProbeDelayMilliseconds { get; set; }

    private int _operatorResponseCount;

    public int OperatorResponseCount => _operatorResponseCount;

    private int _probeRequestCount;

    public int ProbeRequestCount => _probeRequestCount;

    /// <summary>Signaled when an /operator/ handler begins (after hold gate, before delay/response).</summary>
    public TaskCompletionSource<bool>? OperatorHandlerEnteredForTests { get; set; }

    public void HoldNextPanelOperatorProbe()
    {
        _holdNextOperatorResponse = 1;
        _releaseHeldOperatorResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleasePanelOperatorProbeHold()
    {
        _releaseHeldOperatorResponse?.TrySetResult(true);
    }

    public int ProbeSuccessCount
    {
        get
        {
            lock (_telemetryLock)
            {
                return _probeSuccessUtcTimestamps.Count;
            }
        }
    }

  /// <summary>
    /// True when at least one /shell-probe/ 200 was completed while an /operator/ handler was still awaiting its delay.
    /// </summary>
    public bool WasProbeOkServedDuringOperatorDelay
    {
        get
        {
            lock (_telemetryLock)
            {
                foreach (var probeAt in _probeSuccessUtcTimestamps)
                {
                    foreach (var window in _operatorDelayWindows)
                    {
                        if (window.DelayMilliseconds <= 0)
                        {
                            continue;
                        }

                        var windowEnd = window.EndUtc ?? DateTime.UtcNow;
                        if (probeAt >= window.StartUtc && probeAt < windowEnd)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }

    public static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Start()
    {
        if (_listenTask is not null)
        {
            return;
        }

        _listener.Start();
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        _started.Wait(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        Task[] pending;
        lock (_handlerLock)
        {
            pending = _inflightHandlers.ToArray();
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(30));
        }
        catch
        {
        }

        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _cts.Dispose();
        _started.Dispose();
    }

    public string PanelOrigin => $"http://127.0.0.1:{Port}";

    public string OperatorUrl => PanelOrigin + "/operator/";

    public string ProbeUrl => PanelOrigin + "/shell-probe/";

    private async Task ListenLoop(CancellationToken ct)
    {
        _started.Set();
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await Task.Run(() => _listener.GetContext(), ct);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ctx is null)
            {
                continue;
            }

            var handlerTask = Task.Run(() => HandleRequestAsync(ctx, _cts.Token), _cts.Token);
            lock (_handlerLock)
            {
                _inflightHandlers.Add(handlerTask);
            }

            _ = handlerTask.ContinueWith(
                t =>
                {
                    lock (_handlerLock)
                    {
                        _inflightHandlers.Remove(t);
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        OperatorDelayWindow? delayWindow = null;
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/shell-probe", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _probeRequestCount);
                await WriteHtmlResponseAsync(
                    ctx.Response,
                    200,
                    "<html><body>probe ok</body></html>",
                    ProbeDelayMilliseconds,
                    ct);
                lock (_telemetryLock)
                {
                    _probeSuccessUtcTimestamps.Add(DateTime.UtcNow);
                }

                return;
            }

            if (path.StartsWith("/operator", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _operatorResponseCount);
                OperatorHandlerEnteredForTests?.TrySetResult(true);
                if (Interlocked.Exchange(ref _holdNextOperatorResponse, 0) == 1)
                {
                    var release = _releaseHeldOperatorResponse;
                    if (release is not null)
                    {
                        await release.Task.ConfigureAwait(false);
                    }
                }

                var delayMs = OperatorDelayMilliseconds;
                if (delayMs > 0)
                {
                    delayWindow = new OperatorDelayWindow
                    {
                        StartUtc = DateTime.UtcNow,
                        DelayMilliseconds = delayMs,
                    };
                    lock (_telemetryLock)
                    {
                        _operatorDelayWindows.Add(delayWindow);
                    }
                }

                await WriteHtmlResponseAsync(
                    ctx.Response,
                    OperatorStatusCode,
                    OperatorStatusCode == 200
                        ? "<html><body>/operator/ ok</body></html>"
                        : "<html><body>service unavailable</body></html>",
                    delayMs,
                    ct);
                return;
            }

            await WriteHtmlResponseAsync(ctx.Response, 404, "not found", 0, ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                ctx.Response.Abort();
            }
            catch
            {
            }
        }
        finally
        {
            if (delayWindow is not null)
            {
                delayWindow.EndUtc = DateTime.UtcNow;
            }
        }
    }

    private static async Task WriteHtmlResponseAsync(
        HttpListenerResponse response,
        int statusCode,
        string body,
        int delayMilliseconds,
        CancellationToken ct)
    {
        if (delayMilliseconds > 0)
        {
            await Task.Delay(delayMilliseconds, ct);
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }
}
#endif