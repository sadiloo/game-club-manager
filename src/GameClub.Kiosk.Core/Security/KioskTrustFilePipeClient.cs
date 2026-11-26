using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GameClub.Kiosk.Core.Security;

#pragma warning disable CA1416

public sealed class KioskTrustFilePipeClient : IKioskTrustFileClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void WriteTrustFile(string absolutePath, string jsonContent)
    {
        if (!KioskTrustFiles.IsTrustFilePath(absolutePath))
        {
            throw new InvalidOperationException("Guard pipe may only write canonical trust file paths.");
        }

        TrustWriteDiagnostics.LogPipe(absolutePath);
        var fileName = Path.GetFileName(absolutePath);
        var request = KioskTrustFilePipeProtocol.SerializeRequest(fileName, jsonContent);
        var payload = Encoding.UTF8.GetBytes(request);

        using var pipe = new NamedPipeClientStream(
            ".",
            KioskTrustFiles.PipeName,
            PipeDirection.InOut,
            PipeOptions.None);
#if NETSTANDARD2_0
        pipe.Connect(30_000);
#else
        pipe.Connect(TimeSpan.FromSeconds(30));
#endif
        pipe.Write(payload, 0, payload.Length);
        pipe.Flush();
        pipe.WaitForPipeDrain();

        var responseBytes = ReadMessage(pipe);
        var response = JsonSerializer.Deserialize<KioskTrustFilePipeProtocol.PipeResponse>(
            Encoding.UTF8.GetString(responseBytes), JsonOptions);
        if (response?.Ok != true)
        {
            throw new InvalidOperationException(response?.Error ?? "kiosk.trust.write.pipe_failed");
        }
    }

    private static byte[] ReadMessage(NamedPipeClientStream pipe)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (pipe.CanRead)
        {
            var read = pipe.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
            if (!pipe.IsMessageComplete)
            {
                continue;
            }

            break;
        }

        return ms.ToArray();
    }
}

#pragma warning restore CA1416