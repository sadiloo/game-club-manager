using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Guard;

internal static class KioskTrustFileWriter
{
    private static readonly TimeSpan PipeReadTimeout = TimeSpan.FromSeconds(30);

    internal static TimeSpan? TestPipeReadTimeout { get; set; }

    private static TimeSpan EffectiveReadTimeout => TestPipeReadTimeout ?? PipeReadTimeout;

    internal static void Run(CancellationToken ct)
    {
        var expectedSid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (string.IsNullOrWhiteSpace(expectedSid))
        {
            const string msg = "KioskTrustFileWriter: CHECKPOINT_KIOSK_SID is missing; trust pipe will not start.";
            Trace.TraceError(msg);
            try
            {
                if (!EventLog.SourceExists("CheckpointGuard"))
                {
                    EventLog.CreateEventSource("CheckpointGuard", "Application");
                }
                EventLog.WriteEntry("CheckpointGuard", msg, EventLogEntryType.Error, 9001);
            }
            catch
            {
                // best effort alongside Trace
            }
            return;
        }

        var kioskSid = expectedSid!;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreatePipe(kioskSid);
                server.WaitForConnection();
                HandleConnection(server, kioskSid, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                Wait(ct, TimeSpan.FromSeconds(1));
            }
        }
    }

    private static void HandleConnection(NamedPipeServerStream server, string expectedSid, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        string payload;
        try
        {
            payload = ReadString(server, ct);
        }
        catch
        {
            WriteResponse(server, ok: false, "read_failed");
            return;
        }

        string? callerSid = null;
        server.RunAsClient(() =>
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                callerSid = identity.User?.Value;
            }
            catch
            {
                callerSid = null;
            }
        });

        if (!KioskTrustFilePipeProtocol.TryDeserializeRequest(payload, out var fileName, out var content, out var parseError))
        {
            WriteResponse(server, ok: false, parseError ?? "invalid_request");
            return;
        }

        var validationError = KioskTrustFileWriteValidator.ValidatePayload(expectedSid, callerSid, fileName, content);
        if (validationError is not null)
        {
            WriteResponse(server, ok: false, validationError);
            return;
        }

        if (!KioskTrustFileWriteValidator.TryNormalizeTrustFileName(fileName, out var canonical))
        {
            WriteResponse(server, ok: false, "invalid_file_name");
            return;
        }

        try
        {
            var destination = KioskTrustFiles.TrustFilePath(canonical);
            KioskTrustFileAtomicWrite.WriteText(destination, content!);
            WriteResponse(server, ok: true, error: null);
        }
        catch (Exception ex)
        {
            WriteResponse(server, ok: false, ex.GetType().Name);
        }
    }

    private static NamedPipeServerStream CreatePipe(string kioskSid)
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(kioskSid),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return new NamedPipeServerStream(
            KioskTrustFiles.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Message,
            PipeOptions.None,
            4096,
            4096,
            pipeSecurity);
    }

    private static string ReadString(NamedPipeServerStream server, CancellationToken ct)
        => ReadStringAsync(server, ct).GetAwaiter().GetResult();

    private static async Task<string> ReadStringAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(EffectiveReadTimeout);
        var readToken = timeoutCts.Token;

        try
        {
            while (!server.IsMessageComplete)
            {
                var read = await server.ReadAsync(buffer, 0, buffer.Length, readToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (ms.Length + read > KioskTrustFiles.MaxContentBytes)
                {
                    throw new InvalidOperationException("content_too_large");
                }

                ms.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("trust pipe read timed out");
        }

        if (ms.Length > KioskTrustFiles.MaxContentBytes)
        {
            throw new InvalidOperationException("content_too_large");
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteResponse(NamedPipeServerStream server, bool ok, string? error)
    {
        var bytes = KioskTrustFilePipeProtocol.SerializeResponse(ok, error);
        server.Write(bytes, 0, bytes.Length);
        server.Flush();
        server.WaitForPipeDrain();
    }

    private static void Wait(CancellationToken ct, TimeSpan delay)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < delay && !ct.IsCancellationRequested; elapsed += TimeSpan.FromMilliseconds(100))
        {
            Thread.Sleep(100);
        }
    }
}