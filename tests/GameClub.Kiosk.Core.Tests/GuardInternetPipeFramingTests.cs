using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Core.Tests;

[CollectionDefinition(GuardInternetPipeFramingSerialCollection.Name, DisableParallelization = true)]
public sealed class GuardInternetPipeFramingSerialCollection
{
    public const string Name = "guard-internet-pipe-framing-serial";
}

[Collection(GuardInternetPipeFramingSerialCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class GuardInternetPipeFramingTests
{
    [Fact]
    public async Task internet_pipe_line_framing_status_within_2s_while_client_pipe_stays_open()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        using var acceptLoop = LineFramingPipeStub.StartAcceptLoop(LineFramingPipeStub.HandleStatusOrApplyStub);

        var sw = Stopwatch.StartNew();
        var body = await new GuardInternetClient().GetStatusAsync(default);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"status took {sw.ElapsedMilliseconds}ms");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("on", out _));
    }

    [Fact]
    public async Task internet_pipe_line_framing_apply_within_2s_while_client_pipe_stays_open()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        using var acceptLoop = LineFramingPipeStub.StartAcceptLoop(LineFramingPipeStub.HandleStatusOrApplyStub);

        var statusJson = await new GuardInternetClient().GetStatusAsync(default);
        using var statusDoc = JsonDocument.Parse(statusJson);
        var epoch = statusDoc.RootElement.GetProperty("epoch").GetGuid();

        var sw = Stopwatch.StartNew();
        var applyJson = await new GuardInternetClient().ApplyAsync(
            new GameClub.Kiosk.Core.Protocol.SignedInternetCommand
            {
                Payload = new GameClub.Kiosk.Core.Protocol.InternetCommandPayload
                {
                    StationId = "STUB",
                    Epoch = epoch,
                    Version = 1,
                    On = true,
                    IssuedAtUtc = DateTime.UtcNow,
                },
                SignatureBase64 = "stub",
            });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"apply channel took {sw.ElapsedMilliseconds}ms");
        Assert.Contains("error", applyJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task internet_pipe_closes_connection_when_client_never_sends_newline_before_read_timeout()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var priorRead = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS", "400");
        var priorIo = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS", "5000");
        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        var uniquePipe = $"CheckpointGuardInternet-no-newline-{Guid.NewGuid():N}";
        var priorPipe = Environment.GetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable);
        Environment.SetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable, uniquePipe);
        try
        {
            using var cts = new CancellationTokenSource();
            var ready = new ManualResetEventSlim(false);
            var sawTimeout = new ManualResetEventSlim(false);
            var serverLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var pipeThread = new Thread(() => LineFramingPipeStub.RunNoNewlineServer(ready, sawTimeout, cts.Token, serverLog))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "pipe server must signal listening before client connect");

            using var client = new NamedPipeClientStream(".", GuardInternetClient.ResolvePipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(GuardInternetClient.DefaultConnectTimeoutMs);
            var partial = Encoding.UTF8.GetBytes("{\"verb\":\"status\"}");
            try
            {
                await client.WriteAsync(partial, 0, partial.Length);
                await client.FlushAsync();
            }
            catch (IOException) when (sawTimeout.Wait(TimeSpan.FromSeconds(3)) && serverLog.Any(l => l.StartsWith("pipe_read_timeout", StringComparison.Ordinal)))
            {
                /* server closed on purpose (pipe_read_timeout logged, no reply sent) before our write landed */
            }

            Assert.True(sawTimeout.Wait(TimeSpan.FromSeconds(5)), "server read timeout not observed");
            Assert.Contains(serverLog, l => l.StartsWith("pipe_read_timeout", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS", priorRead);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS", priorIo);
            Environment.SetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable, priorPipe);
        }
    }

    [Fact]
    public async Task internet_pipe_guard_writes_nothing_until_full_request_line()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        using var acceptLoop = LineFramingPipeStub.StartAcceptLoop(LineFramingPipeStub.HandleStatusOrApplyStub);

        await GuardInternetPipeOrderingTest.AssertGuardWritesOnlyAfterFullRequestLineAsync(
            connect: async client => { await client.ConnectAsync(3_000); },
            flush: stream => stream.Flush());
    }

    [Fact]
    public async Task internet_pipe_utf8_persian_field_round_trips_on_real_pipe()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        const string persian = "چیپس و نوشابه";
        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        using var acceptLoop = LineFramingPipeStub.StartAcceptLoop(LineFramingPipeStub.HandleEchoNoteStub);

        var requestJson = JsonSerializer.Serialize(new { verb = "status", note = persian });
        var response = await InvokeGuardClientSendAsync(requestJson);
        using var doc = JsonDocument.Parse(response);
        Assert.Equal(persian, doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task internet_pipe_rejects_oversize_request_line()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        var uniquePipe = $"CheckpointGuardInternet-oversize-{Guid.NewGuid():N}";
        var priorPipe = Environment.GetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable);
        Environment.SetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable, uniquePipe);
        try
        {
            using var cts = new CancellationTokenSource();
            var ready = new ManualResetEventSlim(false);
            var sawOversize = new ManualResetEventSlim(false);
            var serverLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var pipeThread = new Thread(() => LineFramingPipeStub.RunOversizeServer(ready, sawOversize, cts.Token, serverLog))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "oversize stub must signal listening before client connect");

            using var client = new NamedPipeClientStream(".", GuardInternetClient.ResolvePipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            var oversize = new string('a', GuardInternetPipeProtocol.MaxLineBytes + 1);
            var line = Encoding.UTF8.GetBytes(oversize + "\n");
            var wroteIOException = false;
            try
            {
                await client.WriteAsync(line, 0, line.Length);
                await client.FlushAsync();
            }
            catch (IOException)
            {
                wroteIOException = true;
            }

            Assert.True(sawOversize.Wait(TimeSpan.FromSeconds(3)));
            var oversizeLogged = serverLog.Any(l => l.StartsWith("pipe_line_oversize", StringComparison.Ordinal));
            Assert.True(oversizeLogged);

            var reply = new byte[256];
            var read = 0;
            try
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                read = await client.ReadAsync(reply, 0, reply.Length, readCts.Token);
            }
            catch (Exception)
            {
                read = 0;
            }

            if (wroteIOException)
            {
                Assert.True(oversizeLogged && read == 0, "IOException on write allowed only after pipe_line_oversize with no reply");
            }
            else
            {
                Assert.True(read == 0, "expected no successful reply after oversize request");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(GuardInternetClient.PipeNameEnvironmentVariable, priorPipe);
        }
    }

    private static Task<string> InvokeGuardClientSendAsync(string requestJson)
    {
        var method = typeof(GuardInternetClient).GetMethod(
            "SendAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task<string>)method!.Invoke(null, new object[] { requestJson, "test", CancellationToken.None })!;
    }
}

internal static class GuardInternetPipeOrderingTest
{
    internal static async Task AssertGuardWritesOnlyAfterFullRequestLineAsync(
        Func<NamedPipeClientStream, Task> connect,
        Action<Stream> flush)
    {
        const string lineBody = "{\"verb\":\"status\"}";
        var encoded = Encoding.UTF8.GetBytes(lineBody);
        var splitAt = 10;
        Assert.True(splitAt < encoded.Length);

        using var client = new NamedPipeClientStream(
            ".",
            GuardInternetClient.ResolvePipeName(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await connect(client);
        await client.WriteAsync(encoded, 0, splitAt);
        flush(client);
        await AssertNoReadableBytesWithinAsync(client, TimeSpan.FromSeconds(1));

        var rest = new byte[encoded.Length - splitAt + 1];
        Array.Copy(encoded, splitAt, rest, 0, encoded.Length - splitAt);
        rest[rest.Length - 1] = (byte)'\n';
        await client.WriteAsync(rest, 0, rest.Length);
        flush(client);

        var responseBytes = await ReadLineBytesWithinAsync(client, TimeSpan.FromSeconds(2));
        Assert.True(responseBytes.Length >= 1);
        if (responseBytes.Length >= 3)
        {
            Assert.False(responseBytes[0] == 0xEF && responseBytes[1] == 0xBB && responseBytes[2] == 0xBF);
        }

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(responseBytes));
        Assert.True(doc.RootElement.TryGetProperty("on", out _));
    }

    private static async Task AssertNoReadableBytesWithinAsync(Stream stream, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource();
        var one = new byte[1];
        var readTask = stream.ReadAsync(one, 0, 1, cts.Token);
        var completed = await Task.WhenAny(readTask, Task.Delay(wait));
        if (completed == readTask)
        {
            var n = await readTask;
            Assert.Equal(0, n);
            return;
        }

        cts.Cancel();
        try
        {
            await readTask;
            Assert.Fail("server returned bytes before the request line was complete");
        }
        catch (OperationCanceledException)
        {
            /* expected: no bytes within the wait window */
        }
    }

    private static async Task<byte[]> ReadLineBytesWithinAsync(Stream stream, TimeSpan wait)
    {
        using var buffer = new MemoryStream();
        var deadline = DateTime.UtcNow.Add(wait);
        var one = new byte[1];
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var readTask = stream.ReadAsync(one, 0, 1);
            if (await Task.WhenAny(readTask, Task.Delay(remaining)) != readTask)
            {
                var partial = Encoding.UTF8.GetString(buffer.ToArray());
                Assert.Fail(
                    $"timed out waiting for response newline after {wait.TotalSeconds:0}s; bytes so far ({buffer.Length}): {partial}");
            }

            var n = await readTask;
            if (n == 0)
            {
                continue;
            }

            if (one[0] == (byte)'\n')
            {
                Assert.True(buffer.Length > 0, "expected a non-empty response line before newline");
                return buffer.ToArray();
            }

            if (one[0] != (byte)'\r')
            {
                buffer.WriteByte(one[0]);
            }
        }

        var tail = Encoding.UTF8.GetString(buffer.ToArray());
        Assert.Fail(
            $"timed out waiting for response newline after {wait.TotalSeconds:0}s; bytes so far ({buffer.Length}): {tail}");
        throw new InvalidOperationException("unreachable");
    }
}

internal static class LineFramingPipeStub
{
    internal sealed class AcceptLoopHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        internal AcceptLoopHandle(Func<string, string> handle)
        {
            InternetPipeOwnerDiagnostics.WriteFixturePhase("stub_fixture_begin", "LineFramingPipeStub.AcceptLoop");
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => RunAcceptLoop(ready, _cts.Token, handle)) { IsBackground = true };
            _thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        }

        public void Dispose()
        {
            InternetPipeOwnerDiagnostics.WriteFixturePhase("stub_fixture_end", "LineFramingPipeStub.AcceptLoop");
            _cts.Cancel();
            try
            {
                using var unblock = new NamedPipeClientStream(".", GuardInternetClient.ResolvePipeName(), PipeDirection.InOut);
                unblock.Connect(1_000);
            }
            catch (Exception)
            {
                /* loop already between instances; it re-checks the token */
            }

            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "accept loop did not stop");
            _cts.Dispose();
        }
    }

    internal static AcceptLoopHandle StartAcceptLoop(Func<string, string> handle) => new(handle);

    internal static void RunAcceptLoop(ManualResetEventSlim ready, CancellationToken ct, Func<string, string> handle)
    {
        ready.Set();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    GuardInternetClient.ResolvePipeName(),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                server.WaitForConnection();
                var line = ReadLine(server, GuardInternetClient.DefaultIoTimeoutMs);
                if (line is null)
                {
                    continue;
                }

                var response = GuardInternetPipeProtocol.FrameRequest(handle(line));
                var bytes = Encoding.UTF8.GetBytes(response);
                server.Write(bytes, 0, bytes.Length);
                server.Flush();
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    internal static void RunNoNewlineServer(ManualResetEventSlim ready, ManualResetEventSlim sawTimeout, CancellationToken ct, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        try
        {
            using var server = new NamedPipeServerStream(
                GuardInternetClient.ResolvePipeName(),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            log.Enqueue("pipe_listening");
            ready.Set();
            server.WaitForConnection();
            var line = ReadLine(server, 1_500, log.Enqueue);
            if (line is null)
            {
                sawTimeout.Set();
            }
        }
        catch
        {
            /* closed */
        }
    }

    internal static void RunOversizeServer(ManualResetEventSlim ready, ManualResetEventSlim sawOversize, CancellationToken ct, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        try
        {
            using var server = new NamedPipeServerStream(
                GuardInternetClient.ResolvePipeName(),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            ready.Set();
            server.WaitForConnection();
            var line = ReadLine(server, 5_000, log.Enqueue);
            if (line is null)
            {
                sawOversize.Set();
            }
        }
        catch
        {
            /* closed */
        }
    }

    internal static string HandleStatusOrApplyStub(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var verb = doc.RootElement.GetProperty("verb").GetString();
        if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                on = true,
                version = 0,
                epoch = Guid.Empty,
            });
        }

        return JsonSerializer.Serialize(new { error = "stub_unsigned_apply" });
    }

    internal static string HandleEchoNoteStub(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var verb = doc.RootElement.GetProperty("verb").GetString();
        if (doc.RootElement.TryGetProperty("note", out var note))
        {
            return JsonSerializer.Serialize(new
            {
                verb,
                note = note.GetString(),
                echoed = true,
            });
        }

        return HandleStatusOrApplyStub(line);
    }

    private static string? ReadLine(Stream stream, int timeoutMs, Action<string>? log = null)
        => GuardInternetPipeLineReader.ReadLine(
            stream,
            timeoutMs,
            GuardInternetPipeProtocol.MaxLineBytes,
            handlerThread: 0,
            appendLog: log);
}
