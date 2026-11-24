using System;
using System.IO;
using System.Text;

namespace GameClub.Kiosk.Core.Guard;

public static class GuardInternetPipeLineReader
{
    public static string? ReadLine(
        Stream stream,
        int timeoutMs,
        int maxLineBytes,
        int handlerThread,
        Action<string>? appendLog)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            int read;
            try
            {
                var readTask = stream.ReadAsync(one, 0, 1);
                if (!readTask.Wait(remaining))
                {
                    appendLog?.Invoke($"pipe_read_timeout thread={handlerThread}");
                    return null;
                }

                read = readTask.Result;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (read == 0)
            {
                appendLog?.Invoke($"pipe_read_eof thread={handlerThread}");
                return null;
            }

            var b = one[0];
            if (b == 0x0A)
            {
                var length = (int)buffer.Length;
                if (length > 0 && buffer.GetBuffer()[length - 1] == 0x0D)
                {
                    length -= 1;
                }

                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, length);
            }

            if (b == 0x0D)
            {
                continue;
            }

            buffer.WriteByte(b);
            if (buffer.Length > maxLineBytes)
            {
                appendLog?.Invoke(
                    $"pipe_line_oversize thread={handlerThread} bytes={buffer.Length}");
                return null;
            }
        }

        appendLog?.Invoke($"pipe_read_timeout thread={handlerThread}");
        return null;
    }
}