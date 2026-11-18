using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

public sealed class GuardInternetClient
{
    public const string PipeName = "CheckpointGuardInternet";

    public Task<string> ApplyAsync(SignedInternetCommand command, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { verb = "apply", command });
        return SendAsync(payload, ct);
    }

    public Task<string> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(JsonSerializer.Serialize(new { verb = "status" }), ct);

    private static async Task<string> SendAsync(string requestJson, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
#if NET10_0_OR_GREATER
        await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
#else
        pipe.Connect(5000);
        await Task.CompletedTask;
#endif
        var req = Encoding.UTF8.GetBytes(requestJson);
        await pipe.WriteAsync(req, 0, req.Length, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var buf = new byte[16_384];
        var read = await pipe.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buf, 0, read);
    }
}