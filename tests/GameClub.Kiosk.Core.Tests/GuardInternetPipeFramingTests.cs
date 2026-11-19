using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Core.Tests;

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

        using var cts = new CancellationTokenSource();
        var ready = new ManualResetEventSlim(false);
        var pipeThread = new Thread(() => LineFramingPipeStub.RunAcceptLoop(ready, cts.Token, LineFramingPipeStub.HandleStatusOrApplyStub))
        {
            IsBackground = true,
            Name = "LineFramingStub",
        };
        pipeThread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

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

        using var cts = new CancellationTokenSource();
        var ready = new ManualResetEventSlim(false);
        var pipeThread = new Thread(() => LineFramingPipeStub.RunAcceptLoop(ready, cts.Token, LineFramingPipeStub.HandleStatusOrApplyStub))
        {
            IsBackground = true,
        };
        pipeThread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

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
        try
        {
            using var cts = new CancellationTokenSource();
            var ready = new ManualResetEventSlim(false);
            var sawTimeout = new ManualResetEventSlim(false);
            var pipeThread = new Thread(() => LineFramingPipeStub.RunNoNewlineServer(ready, sawTimeout, cts.Token))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

            using var client = new NamedPipeClientStream(".", GuardInternetClient.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            var partial = Encoding.UTF8.GetBytes("{\"verb\":\"status\"}");
            await client.WriteAsync(partial, 0, partial.Length);
            await client.FlushAsync();
            Assert.True(sawTimeout.Wait(TimeSpan.FromSeconds(3)), "server read timeout not observed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS", priorRead);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS", priorIo);
        }
    }

    [Fact]
    public async Task internet_pipe_utf8_persian_field_round_trips_on_real_pipe()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        const string persian = "چیپس و نوشابه";
        using var cts = new CancellationTokenSource();
        var ready = new ManualResetEventSlim(false);
        var pipeThread = new Thread(() =>
            LineFramingPipeStub.RunAcceptLoop(ready, cts.Token, LineFramingPipeStub.HandleEchoNoteStub))
        {
            IsBackground = true,
        };
        pipeThread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

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

        using var cts = new CancellationTokenSource();
        var ready = new ManualResetEventSlim(false);
        var sawOversize = new ManualResetEventSlim(false);
        var pipeThread = new Thread(() => LineFramingPipeStub.RunOversizeServer(ready, sawOversize, cts.Token))
        {
            IsBackground = true,
        };
        pipeThread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

        using var client = new NamedPipeClientStream(".", GuardInternetClient.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        var oversize = new string('a', GuardInternetPipeProtocol.MaxLineBytes + 1);
        var line = Encoding.UTF8.GetBytes(oversize + "\n");
        await client.WriteAsync(line, 0, line.Length);
        await client.FlushAsync();
        Assert.True(sawOversize.Wait(TimeSpan.FromSeconds(3)));
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

internal static class LineFramingPipeStub
{
    internal static void RunAcceptLoop(ManualResetEventSlim ready, CancellationToken ct, Func<string, string> handle)
    {
        ready.Set();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    GuardInternetClient.PipeName,
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

    internal static void RunNoNewlineServer(ManualResetEventSlim ready, ManualResetEventSlim sawTimeout, CancellationToken ct)
    {
        ready.Set();
        try
        {
            using var server = new NamedPipeServerStream(
                GuardInternetClient.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            server.WaitForConnection();
            var line = ReadLine(server, 400);
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

    internal static void RunOversizeServer(ManualResetEventSlim ready, ManualResetEventSlim sawOversize, CancellationToken ct)
    {
        ready.Set();
        try
        {
            using var server = new NamedPipeServerStream(
                GuardInternetClient.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            server.WaitForConnection();
            var line = ReadLine(server, 5_000);
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

    private static string? ReadLine(Stream stream, int timeoutMs)
        => GuardInternetPipeLineReader.ReadLine(
            stream,
            timeoutMs,
            GuardInternetPipeProtocol.MaxLineBytes,
            handlerThread: 0,
            appendLog: null);
}
