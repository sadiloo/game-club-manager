using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskTrustFileWriteValidatorTests
{
  private const string KioskSid = "S-1-5-21-1000";

    [Fact]
    public void writer_rejects_bad_caller_sid()
    {
        var err = KioskTrustFileWriteValidator.ValidatePayload(KioskSid, "S-1-5-21-9999", KioskTrustFiles.SetupFileName, "{}");
        Assert.Equal("caller_sid_denied", err);
    }

    [Fact]
    public void writer_rejects_unknown_file_name()
    {
        var err = KioskTrustFileWriteValidator.ValidatePayload(KioskSid, KioskSid, "device.json", "{}");
        Assert.Equal("invalid_file_name", err);
    }

    [Fact]
    public void writer_rejects_oversize_content()
    {
        var big = new string('x', KioskTrustFiles.MaxContentBytes + 8);
        var err = KioskTrustFileWriteValidator.ValidatePayload(KioskSid, KioskSid, KioskTrustFiles.AdminLockFileName, $"{{\"x\":\"{big}\"}}");
        Assert.Equal("content_too_large", err);
    }

    [Fact]
    public void writer_rejects_invalid_json()
    {
        var err = KioskTrustFileWriteValidator.ValidatePayload(KioskSid, KioskSid, KioskTrustFiles.SetupFileName, "{");
        Assert.Equal("invalid_json", err);
    }

    [Fact]
    public void writer_accepts_valid_setup_payload()
    {
        var err = KioskTrustFileWriteValidator.ValidatePayload(KioskSid, KioskSid, KioskTrustFiles.SetupFileName, "{\"a\":1}");
        Assert.Null(err);
    }

    [Fact]
    public void atomic_write_replaces_existing_content()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gcm-trust-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "setup.json");
        try
        {
            KioskTrustFileAtomicWrite.WriteText(path, "{\"v\":1}");
            KioskTrustFileAtomicWrite.WriteText(path, "{\"v\":2}");
            Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}