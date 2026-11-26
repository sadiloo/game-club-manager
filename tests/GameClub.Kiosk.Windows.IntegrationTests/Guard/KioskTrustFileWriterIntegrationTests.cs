#if NET48
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "WindowsGuard")]
public sealed class KioskTrustFileWriterIntegrationTests : IDisposable
{
    private readonly string _priorSid;
    private readonly string _priorRoot;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _server;

    public KioskTrustFileWriterIntegrationTests()
    {
        _priorSid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID") ?? string.Empty;
        _priorRoot = Environment.GetEnvironmentVariable("GAMECLUB_KIOSK_TRUST_ROOT") ?? string.Empty;
        _root = Path.Combine(Path.GetTempPath(), "gcm-trust-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        KioskTrustFiles.TrustRoot = _root;
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", WindowsIdentity.GetCurrent().User!.Value);
        _server = new Thread(() => KioskTrustFileWriter.Run(_cts.Token)) { IsBackground = true, Name = "trust-test" };
        _server.Start();
        WaitForTrustPipeReady(TimeSpan.FromSeconds(5));
    }

    private static void WaitForTrustPipeReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new NamedPipeClientStream(".", KioskTrustFiles.PipeName, PipeDirection.Out);
                probe.Connect(250);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException("trust pipe server not ready", last);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Join(TimeSpan.FromSeconds(2));
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", string.IsNullOrEmpty(_priorSid) ? null : _priorSid);
        Environment.SetEnvironmentVariable(
            "GAMECLUB_KIOSK_TRUST_ROOT",
            string.IsNullOrEmpty(_priorRoot) ? null : _priorRoot);
        KioskTrustFiles.TrustRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk");
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void guard_pipe_writer_accepts_valid_json_from_kiosk_sid()
    {
        var path = KioskTrustFiles.TrustFilePath(KioskTrustFiles.SetupFileName);
        var client = new KioskTrustFilePipeClient();
        var json = "{\"isComplete\":true}";
        client.WriteTrustFile(path, json);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void guard_pipe_rejects_oversize_payload()
    {
        var path = KioskTrustFiles.TrustFilePath(KioskTrustFiles.SetupFileName);
        var client = new KioskTrustFilePipeClient();
        var big = "{\"x\":\"" + new string('a', KioskTrustFiles.MaxContentBytes) + "\"}";
        var err = Assert.Throws<InvalidOperationException>(() => client.WriteTrustFile(path, big));
        Assert.Contains("content_too_large", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void guard_pipe_silent_client_times_out_without_blocking_writer()
    {
        KioskTrustFileWriter.TestPipeReadTimeout = TimeSpan.FromMilliseconds(800);
        try
        {
            var silent = new Thread(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", KioskTrustFiles.PipeName, PipeDirection.InOut);
                    pipe.Connect(5000);
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // expected when server drops timed-out connection
                }
            })
            { IsBackground = true };
            silent.Start();

            var path = KioskTrustFiles.TrustFilePath(KioskTrustFiles.AdminLockFileName);
            var client = new KioskTrustFilePipeClient();
            var json = "{\"locked\":true}";
            client.WriteTrustFile(path, json);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            KioskTrustFileWriter.TestPipeReadTimeout = null;
        }
    }
}
#endif