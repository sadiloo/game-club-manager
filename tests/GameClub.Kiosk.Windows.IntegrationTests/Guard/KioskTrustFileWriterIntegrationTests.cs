#if NET48
using System;
using System.IO;
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
        Thread.Sleep(500);
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
}
#endif