using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private Thread? _thread;

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
        _thread = new Thread(() => loop.Run(token)) { IsBackground = true, Name = "GameClub.Kiosk.Guard" };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _cancel?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
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
    private AdminGate? _admin;
    private string? _shellPath;

    public void Run(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameClub");
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception)
        {
            // The loop retries the database. A missing directory must not kill the service.
        }

        _shellPath = ReadText(Path.Combine(root, "shell.path"));
        try
        {
            var hashPath = Path.Combine(root, "admin-gate.json");
            if (File.Exists(hashPath))
            {
                _admin = new AdminGate(Pbkdf2Hash.FromJson(File.ReadAllText(hashPath)));
                ProtectSecret(hashPath);
                var lockPath = Path.Combine(root, "admin-lockout.json");
                if (File.Exists(lockPath))
                {
                    _admin.RestoreJson(File.ReadAllText(lockPath));
                    ProtectSecret(lockPath);
                }
            }
        }
        catch (Exception)
        {
            _admin = null;
        }

        var listener = new Thread(() => Listen(cancellationToken)) { IsBackground = true, Name = "GameClub.Kiosk.Pipe" };
        listener.Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _store ??= new LocalStore(Path.Combine(root, "kiosk.db"));
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

                var pruned = GuardAdvisor.Prune(_restarts, now, _options.RestartWindow);
                _restarts.Clear();
                _restarts.AddRange(pruned);

                if (decision.EnforceBlocklist)
                {
                    Enforce(now);
                }

                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                try
                {
                    AppendLog(GuardLog.Kill(WindowsSupport.TehranNow(DateTimeOffset.UtcNow), "guard", ex.GetType().Name));
                }
                catch (Exception)
                {
                    // Logging must not take the service down.
                }

                Thread.Sleep(1000);
            }
        }
    }

    private void Listen(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                pipe.WaitForConnection();
                var clientImage = ClientImage(pipe);
                var pending = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while (!cancellationToken.IsCancellationRequested && (read = pipe.Read(chunk, 0, chunk.Length)) > 0)
                {
                    pending.Write(chunk, 0, read);
                    var reply = Consume(pending, clientImage);
                    if (reply is not null)
                    {
                        var bytes = PipeFraming.Encode(new PipeMessage { Kind = "admin-result", Note = reply });
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        break;
                    }
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

    private string? Consume(MemoryStream pending, string? clientImage)
    {
        string? reply = null;
        var bytes = pending.ToArray();
        var offset = 0;
        while (offset < bytes.Length && PipeFraming.TryDecode(Slice(bytes, offset), bytes.Length - offset, out var message, out var consumed))
        {
            if (message is not null)
            {
                reply = Accept(message, clientImage) ?? reply;
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

        return reply;
    }

    private static byte[] Slice(byte[] bytes, int offset)
    {
        var length = bytes.Length - offset;
        var copy = new byte[length];
        Buffer.BlockCopy(bytes, offset, copy, 0, length);
        return copy;
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        foreach (var trustee in PipeAccess.ForKioskUser(ReadText(SidPath())))
        {
            var rights = trustee.Rights == "full" ? PipeAccessRights.FullControl : PipeAccessRights.ReadWrite;
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(trustee.Sid), rights, AccessControlType.Allow));
        }

        var options = PipeOptions.Asynchronous | (PipeOptions)0x00080000;
        return new NamedPipeServerStream("GameClub.Kiosk", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private string? Accept(PipeMessage message, string? clientImage)
    {
        var now = WindowsSupport.TehranNow(DateTimeOffset.UtcNow);
        var kind = message.Kind ?? "";
        lock (_gate)
        {
            if (string.Equals(kind, "heartbeat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "hello", StringComparison.OrdinalIgnoreCase))
            {
                _heartbeat = now;
                return null;
            }

            if (string.Equals(kind, "admin-relock", StringComparison.OrdinalIgnoreCase))
            {
                _escapeUntil = null;
                return "relocked";
            }
        }

        if (!string.Equals(kind, "admin-escape", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "exit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!GuardCommands.TryAuthorizePrivileged(message, clientImage, _shellPath, _admin, now, _options.AdminEscapeDuration, out var until, out var reason))
        {
            PersistLockout();
            AppendLog(GuardLog.Kill(now, clientImage ?? "unknown", "escape_" + reason));
            return reason;
        }

        PersistLockout();
        lock (_gate)
        {
            if (until is DateTimeOffset open)
            {
                _escapeUntil = open;
            }
        }

        return reason;
    }

    private void PersistLockout()
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(DataRoot(), "admin-lockout.json");
            File.WriteAllText(path, _admin.SnapshotJson());
            ProtectSecret(path);
        }
        catch (Exception)
        {
            // A lockout file that cannot be saved must not accept the password anyway.
        }
    }

    private static void ProtectSecret(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        File.SetAccessControl(path, security);
    }

    private static string? ClientImage(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return ImagePath(process);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AppendLog(string line)
    {
        var path = LogPath();
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                var previous = path + ".1";
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }

                File.Move(path, previous);
            }
        }
        catch (Exception)
        {
            // A log that cannot rotate must not stop enforcement.
        }

        File.AppendAllText(path, line + Environment.NewLine);
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
                var decision = GuardEnforcement.Combine(process.ProcessName, elevated: false, allow, context, _store?.ReadStopList());
                if (!decision.Kill)
                {
                    continue;
                }

                process.Kill();
                AppendLog(GuardLog.Kill(now, process.ProcessName, decision.Reason));
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
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

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
