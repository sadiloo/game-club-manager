using System.Text;
using System.Text.Json;

namespace GameClub.Kiosk.Core.Security;

public static class KioskTrustFilePipeProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string SerializeRequest(string fileName, string content)
        => JsonSerializer.Serialize(new PipeRequest(fileName, content), JsonOptions);

    public static bool TryDeserializeRequest(string json, out string? fileName, out string? content, out string? error)
    {
        fileName = null;
        content = null;
        error = null;
        try
        {
            var req = JsonSerializer.Deserialize<PipeRequest>(json, JsonOptions);
            if (req is null || string.IsNullOrWhiteSpace(req.File))
            {
                error = "missing_file";
                return false;
            }

            fileName = req.File;
            content = req.Content ?? "";
            return true;
        }
        catch (JsonException)
        {
            error = "invalid_json";
            return false;
        }
    }

    public static byte[] SerializeResponse(bool ok, string? error)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PipeResponse(ok, error), JsonOptions));

    public sealed record PipeRequest(string File, string? Content);

    public sealed record PipeResponse(bool Ok, string? Error);
}