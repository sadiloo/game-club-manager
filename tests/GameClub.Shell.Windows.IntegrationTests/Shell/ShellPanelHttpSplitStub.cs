#if NET48
using System;
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
    private Task? _listenTask;
    private readonly ManualResetEventSlim _started = new(false);

    public ShellPanelHttpSplitStub(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public int OperatorStatusCode { get; set; } = 503;

    public int OperatorDelayMilliseconds { get; set; }

    private int _operatorResponseCount;

    public int OperatorResponseCount => _operatorResponseCount;

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

            if (ctx is null)
            {
                continue;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/shell-probe", StringComparison.OrdinalIgnoreCase))
            {
                WriteHtmlResponse(ctx.Response, 200, "<html><body>probe ok</body></html>", 0);
                continue;
            }

            if (path.StartsWith("/operator", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _operatorResponseCount);
                var delayMs = OperatorDelayMilliseconds;
                WriteHtmlResponse(
                    ctx.Response,
                    OperatorStatusCode,
                    OperatorStatusCode == 200
                        ? "<html><body>/operator/ ok</body></html>"
                        : "<html><body>service unavailable</body></html>",
                    delayMs);
            }
            else
            {
                WriteHtmlResponse(ctx.Response, 404, "not found", 0);
            }
        }
    }

    private static void WriteHtmlResponse(HttpListenerResponse response, int statusCode, string body, int delayMilliseconds)
    {
        if (delayMilliseconds > 0)
        {
            Thread.Sleep(delayMilliseconds);
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