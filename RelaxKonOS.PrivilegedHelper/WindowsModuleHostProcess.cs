using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Suspended creation under the dedicated primary token; only stdio is inherited, then the job is attached before resume.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsModuleHostProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private readonly ChildPipe _input, _output, _error;
    internal StreamWriter Input { get; }
    internal StreamReader Output { get; }
    internal StreamReader Error { get; }

    internal WindowsModuleHostProcess(WindowsModuleHostAccount.Identity account, string executable, string packageRoot,
        string dataPath, string serviceToken, string? brokerUrl)
    {
        using var impersonation = WindowsS4ULogon.Logon(account.AccountName, Environment.MachineName);
        using (var identity = new WindowsIdentity(impersonation.DangerousGetHandle()))
            if (identity.User?.Value != account.Sid || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("ModuleHost token is not the dedicated ordinary identity.");
        if (!CreateRestrictedToken(impersonation, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (restricted)
        using (var primary = WindowsUserProcessToken.Create(restricted, account.Sid))
        {
            _job = WindowsComponentJob.Create();
            ChildPipe? input = null, output = null, error = null;
            try
            {
                _input = input = new ChildPipe(input: true);
                _output = output = new ChildPipe(input: false);
                _error = error = new ChildPipe(input: false);
                _process = Create(primary, executable, packageRoot, account.ModuleId, dataPath, serviceToken, brokerUrl,
                    _input.Child, _output.Child, _error.Child);
                Input = new StreamWriter(_input.Parent, new UTF8Encoding(false)) { AutoFlush = true };
                Output = new StreamReader(_output.Parent, Encoding.UTF8);
                Error = new StreamReader(_error.Parent, Encoding.UTF8);
            }
            catch { _job.Dispose(); input?.Dispose(); output?.Dispose(); error?.Dispose(); _process?.Dispose(); throw; }
            finally { input?.Child.Dispose(); output?.Child.Dispose(); error?.Child.Dispose(); }
        }
    }
    private SafeFileHandle Create(SafeAccessTokenHandle token, string executable, string packageRoot, string moduleId,
        string dataPath, string serviceToken, string? brokerUrl, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        var arguments = new[] { executable, "--package-root", packageRoot, "--module-id", moduleId, "--data-path", dataPath };
        var command = new StringBuilder(string.Join(" ", arguments.Select(Quote)));
        if (command.Length >= 32767) throw new InvalidDataException();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["WINDIR"] = windows, ["PATH"] = Path.Combine(windows, "System32"),
            ["HOME"] = dataPath, ["USERPROFILE"] = dataPath, ["TEMP"] = Path.Combine(dataPath, "temp"), ["TMP"] = Path.Combine(dataPath, "temp"),
            [ModuleHostProtocol.ServiceTokenEnvironment] = serviceToken,
        };
        if (brokerUrl is not null) environment[ModulePrivilegeProtocol.BrokerEnvironment] = brokerUrl;
        var block = Marshal.StringToHGlobalUni(string.Join('\0', environment.Select(p => p.Key + "=" + p.Value)) + "\0\0");
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var attributes = IntPtr.Zero;
        var initialized = false;
        SafeFileHandle? process = null;
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.Copy(new[] { input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle() }, 0, handles, 3);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = error.DangerousGetHandle() }, Attributes = attributes,
            };
            if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, true,
                0x80000 | 0x400 | 0x8000000 | 0x4, block, dataPath, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = new SafeFileHandle(info.Process, true);
            using var thread = new SafeFileHandle(info.Thread, true);
            if (!AssignProcessToJobObject(_job, process) || ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return process;
        }
        catch { if (process is not null) { TerminateProcess(process, 1); process.Dispose(); } throw; }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(block);
        }
    }
    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = WaitForSingleObject(_process, 0);
            if (status == 0) return;
            if (status != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(100, cancellationToken);
        }
    }
    internal int ExitCode
    {
        get { if (!GetExitCodeProcess(_process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error()); return unchecked((int)code); }
    }
    public void Dispose()
    {
        _job.Dispose();
        _input.Dispose(); _output.Dispose(); _error.Dispose();
        _process.Dispose();
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed class ChildPipe : IDisposable
    {
        internal SafeFileHandle Child { get; }
        internal FileStream Parent { get; }
        internal ChildPipe(bool input)
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out var read, out var write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var parent = input ? write : read;
            Child = input ? read : write;
            try
            {
                if (!SetHandleInformation(parent, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Parent = new FileStream(parent, input ? FileAccess.Write : FileAccess.Read);
            }
            catch { read.Dispose(); write.Dispose(); throw; }
        }
        public void Dispose() { Child.Dispose(); Parent.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disabledCount, IntPtr disabled,
        uint privilegeCount, IntPtr privileges, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application,
        StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}
