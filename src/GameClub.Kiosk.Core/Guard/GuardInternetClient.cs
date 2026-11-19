using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Observability;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

public sealed class GuardInternetClient
{
    public const string PipeName = "CheckpointGuardInternet";
    public const int DefaultConnectTimeoutMs = 5_000;
    public const int DefaultIoTimeoutMs = 30_000;
    /// <summary>Guard server default apply gate wait; must remain below <see cref="DefaultIoTimeoutMs"/>.</summary>
    public const int DefaultServerApplyWaitMs = 20_000;

    public Task<string> ApplyAsync(SignedInternetCommand command, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { verb = "apply", command });
        return SendAsync(payload, "apply", ct);
    }

    public Task<string> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(JsonSerializer.Serialize(new { verb = "status" }), "status", ct);

    private static async Task<string> SendAsync(string requestJson, string verb, CancellationToken ct)
    {
        ObservabilityLogFormats.WriteGuardPipeStep(verb, "start");
        var sw = Stopwatch.StartNew();
        NamedPipeClientStream? pipe = null;
        try
        {
            var connectMs = ResolveTimeoutMs("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", DefaultConnectTimeoutMs);
            var ioMs = ResolveTimeoutMs("CHECKPOINT_INTERNET_PIPE_IO_MS", DefaultIoTimeoutMs);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, $"config connect_ms={connectMs} io_ms={ioMs}");
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "connect_begin");
#if NET10_0_OR_GREATER
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(connectMs);
            try
            {
                await pipe.ConnectAsync(connectMs, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(GuardInternetClientErrorCodes.ConnectTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(GuardInternetClientErrorCodes.ConnectTimeout);
            }
#else
            try
            {
                pipe.Connect(connectMs);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(GuardInternetClientErrorCodes.ConnectTimeout);
            }

            await Task.CompletedTask;
#endif
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "connect_ok");
            var req = Encoding.UTF8.GetBytes(requestJson);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "write_begin");
            await WriteWithTimeoutAsync(pipe, req, 0, req.Length, ioMs, ct).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "write_ok");
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "flush_begin");
            await FlushWithTimeoutAsync(pipe, ioMs, ct).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "flush_ok");
            var buf = new byte[16_384];
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "read_begin");
            var read = await ReadWithTimeoutAsync(pipe, buf, 0, buf.Length, ioMs, ct, verb).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "read_ok");
            var response = Encoding.UTF8.GetString(buf, 0, read);
            ObservabilityLogFormats.WriteGuardPipeStep(verb, "end", (int)sw.ElapsedMilliseconds, errorCode: "ok");
            return response;
        }
        catch (Exception ex)
        {
            var code = GuardInternetClientErrorCodes.MapException(ex);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, $"error_{code}");
            ObservabilityLogFormats.WriteGuardPipeStep(verb, "end", (int)sw.ElapsedMilliseconds, errorCode: code);
            throw;
        }
        finally
        {
            if (pipe is not null)
            {
                ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "dispose_begin");
                pipe.Dispose();
                ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "dispose_ok");
            }
        }
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
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
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
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }
    }

    private static async Task<int> ReadWithTimeoutAsync(
        Stream pipe,
        byte[] buffer,
        int offset,
        int count,
        int timeoutMs,
        CancellationToken ct,
        string verb)
    {
        var readTask = pipe.ReadAsync(buffer, offset, count, ct);
        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (completed != readTask)
        {
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "read_timeout");
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }

        return await readTask.ConfigureAwait(false);
    }
}