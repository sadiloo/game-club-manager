using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

public sealed class GuardInternetClient
{
    public const string PipeName = "CheckpointGuardInternet";
    public const int DefaultConnectTimeoutMs = 5_000;
    public const int DefaultIoTimeoutMs = 30_000;

    public Task<string> ApplyAsync(SignedInternetCommand command, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { verb = "apply", command });
        return SendAsync(payload, ct);
    }

    public Task<string> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(JsonSerializer.Serialize(new { verb = "status" }), ct);

    private static async Task<string> SendAsync(string requestJson, CancellationToken ct)
    {
        var connectMs = ResolveTimeoutMs("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", DefaultConnectTimeoutMs);
        var ioMs = ResolveTimeoutMs("CHECKPOINT_INTERNET_PIPE_IO_MS", DefaultIoTimeoutMs);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
#if NET10_0_OR_GREATER
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(connectMs);
        try
        {
            await pipe.ConnectAsync(connectMs, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("pipe_connect_timeout");
        }
#else
        try
        {
            pipe.Connect(connectMs);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("pipe_connect_timeout");
        }

        await Task.CompletedTask;
#endif
        var req = Encoding.UTF8.GetBytes(requestJson);
        await WriteWithTimeoutAsync(pipe, req, 0, req.Length, ioMs, ct).ConfigureAwait(false);
        await FlushWithTimeoutAsync(pipe, ioMs, ct).ConfigureAwait(false);
        var buf = new byte[16_384];
        var read = await ReadWithTimeoutAsync(pipe, buf, 0, buf.Length, ioMs, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static int ResolveTimeoutMs(string envName, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : fallback;
    }

    private static async Task WriteWithTimeoutAsync(
        Stream pipe,
        byte[] buffer,
        int offset,
        int count,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await pipe.WriteAsync(buffer, offset, count, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("pipe_write_timeout");
        }
    }

    private static async Task FlushWithTimeoutAsync(Stream pipe, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("pipe_write_timeout");
        }
    }

    private static async Task<int> ReadWithTimeoutAsync(
        Stream pipe,
        byte[] buffer,
        int offset,
        int count,
        int timeoutMs,
        CancellationToken ct)
    {
        var readTask = pipe.ReadAsync(buffer, offset, count, ct);
        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (completed != readTask)
        {
            throw new TimeoutException("pipe_read_timeout");
        }

        return await readTask.ConfigureAwait(false);
    }
}