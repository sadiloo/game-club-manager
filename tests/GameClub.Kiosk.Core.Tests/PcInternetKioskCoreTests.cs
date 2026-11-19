using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class PcInternetKioskCoreTests
{
    [Fact]
    public void PcInternet_verify_accepts_valid_signature()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        var payload = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = false,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var canonical = LeaseVerifier.CanonicalJson(payload);
        var sig = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var command = new SignedInternetCommand { Payload = payload, SignatureBase64 = sig };
        var verifier = new InternetCommandVerifier(pub);
        Assert.True(verifier.TryVerify(command, out var err));
        Assert.Equal("", err);
    }

    [Fact]
    public void PcInternet_verify_rejects_forged_signature()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        var payload = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = false,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var command = new SignedInternetCommand { Payload = payload, SignatureBase64 = Convert.ToBase64String(new byte[256]) };
        var verifier = new InternetCommandVerifier(pub);
        Assert.False(verifier.TryVerify(command, out var err));
        Assert.Equal("rejected_signature", err);
    }

    [Fact]
    public void PcInternet_admission_rejects_wrong_station()
    {
        var p = new InternetCommandPayload
        {
            StationId = "PC-OTHER",
            Epoch = Guid.NewGuid(),
            Version = 2,
            On = false,
            IssuedAtUtc = DateTime.UtcNow,
        };
        Assert.Equal("rejected_station", InternetCommandAdmission.TryRejectBeforeApply(p, "PC-1", p.Epoch, 1, null));
    }

    [Fact]
    public void PcInternet_admission_rejects_lower_version_same_epoch()
    {
        var epoch = Guid.NewGuid();
        var p = new InternetCommandPayload { StationId = "PC-1", Epoch = epoch, Version = 1, On = false, IssuedAtUtc = DateTime.UtcNow };
        Assert.Equal("rejected_stale", InternetCommandAdmission.TryRejectBeforeApply(p, "PC-1", epoch, 2, p));
    }

    [Fact]
    public void PcInternet_admission_rejects_older_issue_at_on_epoch_change()
    {
        var older = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = true,
            IssuedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var p = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = true,
            IssuedAtUtc = older.IssuedAtUtc.AddMinutes(-1),
        };
        Assert.Equal("rejected_stale", InternetCommandAdmission.TryRejectBeforeApply(p, "PC-1", older.Epoch, 1, older));
    }

    [Fact]
    public void PcInternet_verified_state_file_atomic_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"internet-state-{Guid.NewGuid():N}.json");
        try
        {
            var p = new InternetCommandPayload
            {
                StationId = "PC-SF",
                Epoch = Guid.NewGuid(),
                Version = 3,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            InternetVerifiedStateFile.Save(path, p);
            var loaded = InternetVerifiedStateFile.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(p.Version, loaded!.Version);
            Assert.Equal(p.On, loaded.On);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PcInternet_kiosk_runtime_records_pipe_client_timeout_as_internet_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prior = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", "200");
        try
        {
            using var rsa = RSA.Create(2048);
            var payload = new InternetCommandPayload
            {
                StationId = "PC-1",
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            var command = new SignedInternetCommand
            {
                Payload = payload,
                SignatureBase64 = Convert.ToBase64String(rsa.SignData(
                    Encoding.UTF8.GetBytes(LeaseVerifier.CanonicalJson(payload)),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1)),
            };
            var runtime = new KioskRuntime(() => new HttpClient());
            var method = typeof(KioskRuntime).GetMethod("ApplyInternetCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = (Task)method!.Invoke(runtime, new object[] { command, CancellationToken.None })!;
            await task.ConfigureAwait(false);
            var field = typeof(KioskRuntime).GetField("_lastInternetReport", BindingFlags.Instance | BindingFlags.NonPublic);
            var report = field!.GetValue(runtime) as KioskInternetAppliedReport;
            Assert.NotNull(report);
            Assert.Equal("pipe_connect_timeout", report!.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", prior);
        }
    }
}