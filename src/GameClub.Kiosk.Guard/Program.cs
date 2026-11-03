using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using GameClub.Kiosk.Core;

namespace GameClub.Kiosk.Guard;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && string.Equals(args[0], "hash-admin-file", StringComparison.OrdinalIgnoreCase))
        {
            var password = File.ReadAllText(args[1]).Trim();
            var directory = Path.GetDirectoryName(args[2]);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(args[2], Pbkdf2Hash.Create(password).ToJson());
            return 0;
        }

        if (args.Any(arg => string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)))
        {
            using var worker = new GuardLoop();
            worker.Run(CancellationToken.None);
            return 0;
        }

        ServiceBase.Run(new GuardService());
        return 0;
    }
}

internal sealed class GuardService : ServiceBase
{
    private GuardLoop? _loop;
    private CancellationTokenSource? _cancel;

    public GuardService()
    {
        ServiceName = "GameClubKioskGuard";
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _cancel = new CancellationTokenSource();
        _loop = new GuardLoop();
        var token = _cancel.Token;
        var loop = _loop;
        var thread = new Thread(() => loop.Run(token)) { IsBackground = true, Name = "GameClub.Kiosk.Guard" };
        thread.Start();
    }

    protected override void OnStop()
    {
        _cancel?.Cancel();
        _loop?.Dispose();
    }
}

internal sealed class GuardLoop : IDisposable
{
    private readonly GuardOptions _options = new();
    private readonly List<DateTimeOffset> _restarts = [];
    private readonly object _gate = new();
    private DateTimeOffset? _heartbeat;
    private DateTimeOffset? _escapeUntil;
    private LocalStore? _store;

    public void Run(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub");
        Directory.CreateDirectory(root);
        _store = new LocalStore(Path.Combine(root, "kiosk.db"));
        var listener = new Thread(() => Listen(cancellationToken)) { IsBackground = true, Name = "GameClub.Kiosk.Pipe" };
        listener.Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
            DateTimeOffset? heartbeat;
            DateTimeOffset? escape;
            lock (_gate)
            {
                heartbeat = _heartbeat;
                escape = _escapeUntil;
            }

            var decision = GuardAdvisor.Decide(
                new GuardSnapshot(ShellIsRunning(), heartbeat, escape, _restarts.ToArray()),
                now,
                _options);
            if (decision.RestartShell && StartShell())
            {
                _restarts.Add(now);
                lock (_gate)
                {
                    _heartbeat = null;
                }
            }

            if (decision.EnforceBlocklist)
            {
                Enforce(now);
            }

            Thread.Sleep(1000);
        }
    }

    private void Listen(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream("GameClub.Kiosk", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnection();
                var pending = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while (!cancellationToken.IsCancellationRequested && (read = pipe.Read(chunk, 0, chunk.Length)) > 0)
                {
                    pending.Write(chunk, 0, read);
                    Consume(pending);
                }
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void Consume(MemoryStream pending)
    {
        var bytes = pending.ToArray();
        var offset = 0;
        while (offset < bytes.Length && PipeFraming.TryDecode(Slice(bytes, offset), bytes.Length - offset, out var message, out var consumed))
        {
            if (message is not null)
            {
                Accept(message);
            }

            if (consumed <= 0)
            {
                break;
            }

            offset += consumed;
        }

        pending.SetLength(0);
        if (offset < bytes.Length)
        {
            pending.Write(bytes, offset, bytes.Length - offset);
        }
    }

    private static byte[] Slice(byte[] bytes, int offset)
    {
        var length = bytes.Length - offset;
        var copy = new byte[length];
        Buffer.BlockCopy(bytes, offset, copy, 0, length);
        return copy;
    }

    private void Accept(PipeMessage message)
    {
        var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
        lock (_gate)
        {
            if (string.Equals(message.Kind, "heartbeat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Kind, "hello", StringComparison.OrdinalIgnoreCase))
            {
                _heartbeat = now;
            }
            else if (string.Equals(message.Kind, "admin-escape", StringComparison.OrdinalIgnoreCase))
            {
                _escapeUntil = now.Add(_options.AdminEscapeDuration);
            }
            else if (string.Equals(message.Kind, "admin-relock", StringComparison.OrdinalIgnoreCase))
            {
                _escapeUntil = null;
            }
        }
    }

    private void Enforce(DateTimeOffset now)
    {
        var allow = _store is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : GameProcesses.AllowSet(_store.ReadCatalog());
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var decision = ProcessPolicy.Evaluate(process.ProcessName, IsElevated(process), allow);
                if (!decision.Kill)
                {
                    continue;
                }

                process.Kill();
                File.AppendAllText(LogPath(), now.ToString("o") + " killed " + process.ProcessName + " " + decision.Reason + Environment.NewLine);
            }
            catch (Exception)
            {
                // A protected process, or one that already exited, is not a kiosk failure.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool ShellIsRunning()
    {
        var processes = Process.GetProcessesByName("GameClub.Kiosk");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool StartShell()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub", "shell.path");
        if (!File.Exists(path))
        {
            return false;
        }

        var shell = File.ReadAllText(path).Trim();
        if (shell.Length == 0 || !File.Exists(shell))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo { FileName = shell, UseShellExecute = true });
        return true;
    }

    private static string LogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub", "guard.log");

    private static bool IsElevated(Process process)
    {
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
        {
            return false;
        }

        try
        {
            var elevation = 0;
            var size = Marshal.SizeOf(elevation);
            var written = 0;
            if (!GetTokenInformation(token, 20, ref elevation, size, ref written))
            {
                return false;
            }

            return elevation != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public void Dispose() => _store?.Dispose();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref int tokenInformation, int tokenInformationLength, ref int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
