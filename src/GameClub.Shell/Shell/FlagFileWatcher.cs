using System;
using System.IO;
using System.Threading;

namespace GameClub.Shell.Shell;

internal sealed class FlagFileWatcher : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private string? _lastDisk;
    private string? _lastBackup;

    public event Action<string, string>? FlagBalloon;

    public FlagFileWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }

    private void Poll()
    {
        ReadFlag(Path.Combine(ShellPaths.ProgramDataLogs, "disk-warning.flag"), ref _lastDisk);
        ReadFlag(Path.Combine(ShellPaths.ProgramDataLogs, "backup-secondary-missing.flag"), ref _lastBackup);
    }

    private void ReadFlag(string path, ref string? last)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var text = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(text) || text == last)
            {
                return;
            }

            last = text;
            FlagBalloon?.Invoke("هشدار چک‌پوینت", text);
        }
        catch
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}