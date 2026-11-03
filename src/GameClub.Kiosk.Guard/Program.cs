using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
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
        var console = ConsoleSession();
        var kioskSid = ReadText(SidPath());
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var context = new ProcessContext
                {
                    SessionId = process.SessionId,
                    KioskSessionId = console,
                    UserSid = ProcessSid(process),
                    KioskUserSid = kioskSid,
                    OriginalFileName = OriginalFileName(process),
                    ImagePath = ImagePath(process),
                };
                var decision = ProcessPolicy.Evaluate(process.ProcessName, elevated: false, allow, context);
                if (!decision.Kill)
                {
                    continue;
                }

                process.Kill();
                File.AppendAllText(LogPath(), GuardLog.Kill(now, process.ProcessName, decision.Reason) + Environment.NewLine);
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
        var console = ConsoleSession();
        var kioskSid = ReadText(SidPath());
        var processes = Process.GetProcessesByName("GameClub.Kiosk");
        try
        {
            foreach (var process in processes)
            {
                if (InteractiveSession.IsKioskShell(process.SessionId, console, ProcessSid(process), kioskSid))
                {
                    return true;
                }
            }

            return false;
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
        var shell = ReadText(Path.Combine(DataRoot(), "shell.path"));
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
        {
            return false;
        }

        var session = ConsoleSession();
        if (session <= 0)
        {
            return false;
        }

        if (!WTSQueryUserToken(session, out var token))
        {
            return false;
        }

        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), lpDesktop = InteractiveSession.Desktop };
            var command = "\"" + shell + "\"";
            if (!CreateProcessAsUser(token, null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out var process))
            {
                return false;
            }

            CloseHandle(process.hProcess);
            CloseHandle(process.hThread);
            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static int ConsoleSession()
    {
        var session = WTSGetActiveConsoleSessionId();
        return session == unchecked((int)0xFFFFFFFF) ? 0 : session;
    }

    private static string? ProcessSid(Process process)
    {
        try
        {
            if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            {
                return null;
            }

            try
            {
                var needed = 0;
                GetTokenInformation(token, 1, IntPtr.Zero, 0, ref needed);
                if (needed <= 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(token, 1, buffer, needed, ref needed))
                    {
                        return null;
                    }

                    var sid = Marshal.ReadIntPtr(buffer);
                    if (!ConvertSidToStringSid(sid, out var text))
                    {
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(text);
                    }
                    finally
                    {
                        LocalFree(text);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ImagePath(Process process)
    {
        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(process.Handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? OriginalFileName(Process process)
    {
        var path = ImagePath(process);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).OriginalFilename;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub");

    private static string SidPath() => Path.Combine(DataRoot(), "kiosk.sid");

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string LogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub", "guard.log");

    public void Dispose() => _store?.Dispose();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, ref int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? application,
        string command,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint flags,
        IntPtr environment,
        string? directory,
        ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
