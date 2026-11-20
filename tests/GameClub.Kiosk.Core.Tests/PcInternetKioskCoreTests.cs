using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class PcInternetKioskCoreTests
{
    [Fact]
    public void PcInternet_guard_client_wire_apply_on_is_classified_as_on_request()
    {
        using var rsa = RSA.Create(2048);
        var onPayload = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 2,
            On = true,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var offPayload = new InternetCommandPayload
        {
            StationId = onPayload.StationId,
            Epoch = onPayload.Epoch,
            Version = 1,
            On = false,
            IssuedAtUtc = onPayload.IssuedAtUtc,
        };
        var onCommand = new SignedInternetCommand
        {
            Payload = onPayload,
            SignatureBase64 = Convert.ToBase64String(new byte[32]),
        };
        var offCommand = new SignedInternetCommand
        {
            Payload = offPayload,
            SignatureBase64 = Convert.ToBase64String(new byte[32]),
        };
        var onWire = JsonSerializer.Serialize(new { verb = "apply", command = onCommand });
        var offWire = JsonSerializer.Serialize(new { verb = "apply", command = offCommand });
        Assert.True(GuardInternetPipeApplyJson.IsOnApplyRequest(onWire));
        Assert.False(GuardInternetPipeApplyJson.IsOnApplyRequest(offWire));
        var statusWire = JsonSerializer.Serialize(new { verb = "status" });
        Assert.True(GuardInternetPipeApplyJson.IsStatusRequest(statusWire));
        Assert.False(GuardInternetPipeApplyJson.RequiresMutatingApplyFifo(statusWire));
        Assert.True(GuardInternetPipeApplyJson.RequiresMutatingApplyFifo(onWire));
    }

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
    public void PcInternet_server_apply_wait_default_is_below_client_io_timeout()
    {
        Assert.Equal(30_000, GuardInternetClient.DefaultIoTimeoutMs);
        Assert.True(GuardInternetBudgetDefaults.ApplyGateWaitFitsClientIo());
        Assert.True(GuardInternetClient.DefaultIoTimeoutMs - GuardInternetClient.DefaultServerApplyWaitMs >= 5_000);
    }

    [Fact]
    public void PcInternet_apply_powershell_worst_case_with_kill_waits_fits_client_io()
    {
        Assert.Equal(30_000, GuardInternetClient.DefaultIoTimeoutMs);
        Assert.True(GuardInternetBudgetDefaults.ApplyPathWorstCaseFitsClientIo());
        Assert.Equal(
            GuardInternetBudgetDefaults.ApplyPathPowerShellBudgetSumMs()
            + (GuardInternetBudgetDefaults.ApplyPathPowerShellCallCountWorstCase
               * GuardInternetBudgetDefaults.PowerShellKillAfterExitWaitMs),
            GuardInternetBudgetDefaults.ApplyPathWorstCaseWithKillWaitsMs());
    }

    [Fact]
    public void PcInternet_reconcile_powershell_worst_case_with_kill_waits_fits_wall_with_margin()
    {
        Assert.Equal(120_000, GuardInternetBudgetDefaults.ReconcileWallTimeoutMs);
        Assert.True(GuardInternetBudgetDefaults.ReconcileWorstCaseFitsWallWithMargin());
        Assert.True(
            GuardInternetBudgetDefaults.ReconcileWallTimeoutMs
            - GuardInternetBudgetDefaults.ReconcileWorstCaseWithKillWaitsMs()
            >= GuardInternetBudgetDefaults.ReconcilePlanMarginMs);
    }

    [Fact]
    public async Task PcInternet_kiosk_runtime_records_pipe_client_timeout_as_internet_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope();
        try
        {
            using var probe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                GuardInternetClient.PipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            probe.Connect(300);
            Assert.Fail($"{GuardInternetClient.PipeName} must have no listener for connect-timeout test.");
        }
        catch (TimeoutException)
        {
            /* expected: no guard pipe server */
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
            await task;
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

    [Fact]
    public void PcInternet_guard_client_error_mapper_maps_io_and_generic_errors()
    {
        Assert.Equal("pipe_connect_timeout", GuardInternetClientErrorCodes.MapException(new TimeoutException("pipe_connect_timeout")));
        Assert.Equal("pipe_io_timeout", GuardInternetClientErrorCodes.MapException(new TimeoutException("pipe_io_timeout")));
        Assert.Equal("pipe_io_timeout", GuardInternetClientErrorCodes.MapException(new TimeoutException("pipe_read_timeout")));
        Assert.Equal("pipe_connect_timeout", GuardInternetClientErrorCodes.MapException(new TimeoutException("The operation has timed out.")));
        Assert.Equal("pipe_error", GuardInternetClientErrorCodes.MapException(new InvalidOperationException("boom")));
    }
}