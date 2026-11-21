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
    public const string PipeNameEnvironmentVariable = "CHECKPOINT_INTERNET_PIPE_NAME";
    public const int DefaultConnectTimeoutMs = 5_000;
    /// <summary>Guard server read line budget for a small framed request (not client response IO).</summary>
    public const int DefaultPipeServerReadTimeoutMs = 30_000;
    /// <summary>
    /// Must cover <see cref="GuardInternetBudgetDefaults.MutatingApplyFifoQueueDepthWorstCase"/> serial
    /// apply paths during early-reassert hold (see <see cref="GuardInternetBudgetDefaults.MutatingApplyFifoClientIoBudgetMs"/>).
    /// </summary>
    public const int DefaultIoTimeoutMs = 55_000;
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
            pipe = new NamedPipeClientStream(".", ResolvePipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
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
            var req = Encoding.UTF8.GetBytes(GuardInternetPipeProtocol.FrameRequest(requestJson));
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "write_begin");
            await WriteWithTimeoutAsync(pipe, req, 0, req.Length, ioMs, ct).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "write_ok");
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "flush_begin");
            await FlushWithTimeoutAsync(pipe, ioMs, ct).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "flush_ok");
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "read_begin");
            var response = await ReadLineWithTimeoutAsync(pipe, ioMs, ct).ConfigureAwait(false);
            ObservabilityLogFormats.WriteGuardPipeSubStep(verb, "read_ok");
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

    public static string ResolvePipeName()
    {
        var raw = Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable)?.Trim();
        return string.IsNullOrWhiteSpace(raw) ? PipeName : raw!;
    }

    private static async Task WriteWithTimeoutAsync(
        Stream pipe,
        byte[] buffer,
        int offset,
        int count,
        int timeoutMs,
        CancellationToken ct)
    {
#if NET10_0_OR_GREATER
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
#else
        var writeTask = Task.Factory.FromAsync(
            pipe.BeginWrite,
            pipe.EndWrite,
            buffer,
            offset,
            count,
            null);
        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = await Task.WhenAny(writeTask, delayTask).ConfigureAwait(false);
        if (completed != writeTask)
        {
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }

        await writeTask.ConfigureAwait(false);
#endif
    }

    private static async Task FlushWithTimeoutAsync(Stream pipe, int timeoutMs, CancellationToken ct)
    {
#if NET10_0_OR_GREATER
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
#else
        var flushTask = Task.Run(() => pipe.Flush(), ct);
        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = await Task.WhenAny(flushTask, delayTask).ConfigureAwait(false);
        if (completed != flushTask)
        {
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }

        await flushTask.ConfigureAwait(false);
#endif
    }

    private static async Task<string> ReadLineWithTimeoutAsync(Stream pipe, int timeoutMs, CancellationToken ct)
    {
#if NET10_0_OR_GREATER
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
            }

            if (Encoding.UTF8.GetByteCount(line) > GuardInternetPipeProtocol.MaxLineBytes)
            {
                throw new InvalidOperationException(GuardInternetClientErrorCodes.PipeError);
            }

            return line;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }
#else
        var readTask = Task.Run(
            () =>
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                return reader.ReadLine();
            },
            ct);
        var delayTask = Task.Delay(timeoutMs, ct);
        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (completed != readTask)
        {
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }

        var line = await readTask.ConfigureAwait(false);
        if (line is null)
        {
            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
        }

        if (Encoding.UTF8.GetByteCount(line) > GuardInternetPipeProtocol.MaxLineBytes)
        {
            throw new InvalidOperationException(GuardInternetClientErrorCodes.PipeError);
        }

        return line;
#endif
    }
}