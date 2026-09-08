using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// Windows ConPTY-based <see cref="IPty"/> implementation with the correct Win32 signatures.
/// The bundled RoyalApps.RoyalTerminal.Terminal.Pty.Windows 0.4.0 declares CreatePseudoConsole
/// with the wrong parameter order (COORD size first), so on x64 the pseudo console is never
/// actually created: the shell falls back to inherited standard handles, output never reaches
/// the ConPTY pipes (DataReceived stays silent) and written input goes nowhere.
/// </summary>
public sealed class ConPty : IPty, IDisposable
{
    private const uint ProcThreadAttributePseudoConsole = 0x20016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int BufferSize = 64 * 1024;

    private readonly BlockingCollection<byte[]> _pendingWrites = new();

    private CancellationTokenSource? _cts;
    private IntPtr _ptyHandle;
    private IntPtr _processHandle;
    private SafeFileHandle? _inputHandle;
    private SafeFileHandle? _outputHandle;
    private Stream? _inputStream;
    private Thread? _readThread;
    private Thread? _writeThread;
    private int _childPid;
    private bool _disposed;
    private int _disposeSignaled;

    public bool IsRunning { get; set; }

    public int ChildPid
    {
        get => _childPid;
        set => _childPid = value;
    }

    public event Action<byte[], int>? DataReceived;
    public event Action<int>? ProcessExited;

    public void Start(string? shell, int columns, int rows, string? workingDirectory,
        Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cts is not null)
            throw new InvalidOperationException("PTY is already started.");

        var sa = new SecurityAttributes(inheritHandle: true);
        if (!Native.CreatePipe(out var hInRead, out var hInWrite, ref sa, 0) ||
            !Native.CreatePipe(out var hOutRead, out var hOutWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var hr = Native.CreatePseudoConsole(
                new Coord
                {
                    X = checked((short)columns),
                    Y = checked((short)rows)
                },
                hInRead,
                hOutWrite,
                0,
                out var hpc);
            if (hr != 0)
                throw new Win32Exception(hr);

            _ptyHandle = hpc;
            _inputHandle = new SafeFileHandle(hInWrite, ownsHandle: true);
            _outputHandle = new SafeFileHandle(hOutRead, ownsHandle: true);
            hInWrite = IntPtr.Zero;
            hOutRead = IntPtr.Zero;

            _inputStream = new FileStream(_inputHandle, FileAccess.Write, 0, isAsync: false);
            LaunchProcess(shell, workingDirectory, environment, arguments);

            IsRunning = true;
            _cts = new CancellationTokenSource();
            _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "ConPty-Write" };
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPty-Read" };
            _writeThread.Start();
            _readThread.Start();
        }
        catch
        {
            Cleanup();
            throw;
        }
        finally
        {
            // CreatePseudoConsole duplicates the ends it needs; close our remaining local copies.
            if (hInRead != IntPtr.Zero) Native.CloseHandle(hInRead);
            if (hInWrite != IntPtr.Zero) Native.CloseHandle(hInWrite);
            if (hOutRead != IntPtr.Zero) Native.CloseHandle(hOutRead);
            if (hOutWrite != IntPtr.Zero) Native.CloseHandle(hOutWrite);
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (!IsRunning || count <= 0)
            return;
        var chunk = new byte[count];
        Buffer.BlockCopy(data, offset, chunk, 0, count);
        _pendingWrites.Add(chunk);
    }

    public void Write(string text)
    {
        if (!IsRunning || string.IsNullOrEmpty(text))
            return;
        Write(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text));
    }

    public void Resize(int columns, int rows)
    {
        if (_ptyHandle == IntPtr.Zero)
            return;
        Native.ResizePseudoConsole(_ptyHandle, new Coord { X = checked((short)columns), Y = checked((short)rows) });
    }

    public void Resize(int columns, int rows, int widthPixels, int heightPixels) => Resize(columns, rows);

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;
        IsRunning = false;
        _cts?.Cancel();
        try { _pendingWrites.CompleteAdding(); } catch { /* already completed */ }

        if (_processHandle != IntPtr.Zero)
        {
            try { Native.TerminateProcess(_processHandle, 1); } catch { /* best effort */ }
        }
        if (_ptyHandle != IntPtr.Zero)
        {
            try { Native.ClosePseudoConsole(_ptyHandle); } catch { /* best effort */ }
            _ptyHandle = IntPtr.Zero;
        }
        try { _inputStream?.Dispose(); } catch { /* best effort */ }
        _inputStream = null;
        try { _inputHandle?.Dispose(); } catch { /* best effort */ }
        _inputHandle = null;
        try { _outputHandle?.Dispose(); } catch { /* best effort */ }
        _outputHandle = null;

        _writeThread?.Join(2000);
        _readThread?.Join(2000);

        if (_processHandle != IntPtr.Zero)
        {
            Native.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        _cts?.Dispose();
        _cts = null;
        _disposed = true;
    }

    public void Dispose() => Stop();

    private void LaunchProcess(string? shell, string? workingDirectory,
        Dictionary<string, string>? environment, IReadOnlyList<string>? arguments)
    {
        var commandLine = BuildCommandLine(string.IsNullOrWhiteSpace(shell) ? "powershell" : shell!, arguments);
        var envBlock = BuildEnvironmentBlock(environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var attrList = IntPtr.Zero;
        try
        {
            var si = new StartupInfoEx { StartupInfo = { Cb = Marshal.SizeOf<StartupInfoEx>() } };
            var attrSize = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    _ptyHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            si.AttributeList = attrList;

            var cwd = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                ? null
                : workingDirectory;

            if (!Native.CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                    false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, envBlock, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Native.CloseHandle(pi.Thread);
            _processHandle = pi.Process;
            _childPid = pi.ProcessId;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (envBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(envBlock);
        }
    }

    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                var chunk = _pendingWrites.Take(_cts?.Token ?? CancellationToken.None);
                try
                {
                    _inputStream?.Write(chunk, 0, chunk.Length);
                    _inputStream?.Flush();
                }
                catch
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private void ReadLoop()
    {
        var buffer = new byte[BufferSize];
        var exitCode = -1;
        try
        {
            using var stream = new FileStream(_outputHandle!, FileAccess.Read, 0, isAsync: false);
            while (_cts is not null && !_cts.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read <= 0)
                    break;

                var snapshot = new byte[read];
                Buffer.BlockCopy(buffer, 0, snapshot, 0, read);
                try { DataReceived?.Invoke(snapshot, read); } catch { /* keep reading */ }
            }
        }
        catch
        {
            // Pipe teardown races are expected during Stop.
        }

        try
        {
            if (_processHandle != IntPtr.Zero)
            {
                Native.WaitForSingleObject(_processHandle, 1000);
                if (Native.GetExitCodeProcess(_processHandle, out var code) && code != 259 /* STILL_ACTIVE */)
                    exitCode = (int)code;
            }
        }
        catch { }

        IsRunning = false;
        try { ProcessExited?.Invoke(exitCode); } catch { /* handlers must not crash the loop */ }
    }

    private void Cleanup()
    {
        if (_ptyHandle != IntPtr.Zero)
        {
            try { Native.ClosePseudoConsole(_ptyHandle); } catch { }
            _ptyHandle = IntPtr.Zero;
        }
        try { _inputStream?.Dispose(); } catch { }
        _inputStream = null;
        try { _inputHandle?.Dispose(); } catch { }
        _inputHandle = null;
        try { _outputHandle?.Dispose(); } catch { }
        _outputHandle = null;
        _pendingWrites.Dispose();
        _disposed = true;
    }

    private static string BuildCommandLine(string shell, IReadOnlyList<string>? arguments)
    {
        var sb = new StringBuilder(EscapeArgument(shell));
        if (arguments is not null)
        {
            foreach (var arg in arguments)
                sb.Append(' ').Append(EscapeArgument(arg));
        }
        return sb.ToString();
    }

    private static string EscapeArgument(string arg)
    {
        var sb = new StringBuilder();
        var needsQuotes = arg.Any(c => c is ' ' or '\t' or '"');
        if (needsQuotes) sb.Append('"');

        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
        }

        if (needsQuotes)
        {
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }
        else
        {
            sb.Append('\\', backslashes);
        }
        return sb.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(Dictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var kv in environment)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;

        public SecurityAttributes(bool inheritHandle)
        {
            Length = Marshal.SizeOf<SecurityAttributes>();
            SecurityDescriptor = IntPtr.Zero;
            InheritHandle = inheritHandle;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Ptr;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SecurityAttributes lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        public static extern int CreatePseudoConsole(
            Coord size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList,
            int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags,
            IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
