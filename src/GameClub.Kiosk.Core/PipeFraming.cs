using System.Text;
using System.Text.Json;

namespace GameClub.Kiosk.Core;

public sealed class PipeMessage
{
    public string Kind { get; set; } = "";

    public string? SessionId { get; set; }

    public long UnixMilliseconds { get; set; }

    public string? Note { get; set; }
}

public static class PipeFraming
{
    public const int MaxPayload = 1024 * 1024;

    public static byte[] Encode(PipeMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        return EncodeJson(json);
    }

    public static byte[] EncodeJson(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxPayload)
        {
            throw new InvalidOperationException("Pipe payload is too large.");
        }

        var packet = new byte[4 + body.Length];
        WriteInt32(packet, 0, body.Length);
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        return packet;
    }

    public static bool TryDecode(byte[] buffer, int count, out PipeMessage? message, out int consumed)
    {
        message = null;
        if (!TryDecodeJson(buffer, count, out var json, out consumed) || json is null)
        {
            return false;
        }

        message = JsonSerializer.Deserialize<PipeMessage>(json);
        return message is not null;
    }

    public static bool TryDecodeJson(byte[] buffer, int count, out string? json, out int consumed)
    {
        json = null;
        consumed = 0;
        if (buffer is null || count < 4)
        {
            return false;
        }

        var length = ReadInt32(buffer, 0);
        if (length < 0 || length > MaxPayload)
        {
            throw new InvalidOperationException("Bad pipe frame.");
        }

        if (count < 4 + length)
        {
            return false;
        }

        json = Encoding.UTF8.GetString(buffer, 4, length);
        consumed = 4 + length;
        return true;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] buffer, int offset)
    {
        return buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24);
    }
}
