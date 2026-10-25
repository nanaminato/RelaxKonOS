using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>A closed Git runner. Never use Process.Start while impersonating: it would create
/// a child with the Helper's LocalSystem process token, not the thread's user token.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUserGit
{
    internal sealed record Result(bool Success, int ExitCode, string Output, string Error);
    internal sealed class OutputTooLargeException : Exception;

    internal static Result Execute(SafeAccessTokenHandle impersonation, UserExecutionRequest request,
        string home, CancellationToken cancellationToken)
    {
        if (!UserExecutionGitPolicy.IsAllowed(request.GitArguments)
            || request.GitCredentials is { } credentials && !credentials.IsValid())
            throw new ArgumentException("Invalid Git request.");
        cancellationToken.ThrowIfCancellationRequested();
        using var primary = WindowsUserProcessToken.Create(impersonation, request.Identity.StableIdentity);
        var environment = TerminalUserEnvironment.Windows(primary, home);
        // Neither the Helper's profile, PATH, Git overrides nor secrets are inherited.
        foreach (var key in environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("RELAXKONOS_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase)).ToArray()) environment.Remove(key);
        environment["HOME"] = home;
        environment["GCM_INTERACTIVE"] = "never";
        var temp = Path.Combine(home, "AppData", "Local", "Temp");
        environment["TEMP"] = environment["TMP"] = temp;
        var scopeDirectory = Path.Combine(temp, $"relaxkonos-git-{Guid.NewGuid():N}");
        GitAskPassScope? askPass = null;
        try
        {
            var setup = WindowsIdentity.RunImpersonated(impersonation, () =>
            {
                if (request.Path is null || !Path.IsPathFullyQualified(request.Path)) throw new ArgumentException("Absolute Git working directory required.");
                var workingDirectory = Path.GetFullPath(request.Path);
                if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException();
                var git = ResolveGit(environment);
                Directory.CreateDirectory(temp);
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm($"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{request.Identity.StableIdentity})");
                new DirectoryInfo(scopeDirectory).Create(security);
                askPass = GitAskPassScope.Create(request.GitCredentials?.Username, request.GitCredentials?.Password, scopeDirectory);
                return (git, workingDirectory);
            });
            var startInfo = new ProcessStartInfo(setup.git);
            startInfo.Environment.Clear();
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
            UserExecutionGitPolicy.ApplySafeEnvironment(startInfo);
            askPass?.ApplyTo(startInfo);
            return Run(primary, setup.git, request.GitArguments!, setup.workingDirectory,
                startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!), cancellationToken);
        }
        finally
        {
            // Run has closed its kill-on-close job before the credential script is removed.
            WindowsIdentity.RunImpersonated(impersonation, () =>
            {
                askPass?.Dispose();
                try { Directory.Delete(scopeDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            });
        }
    }

    internal static string ResolveGit(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.TryGetValue("PATH", out var path))
            foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidateDirectory = directory.Trim().Trim('"');
                // Do not search the service's cwd or execute a .cmd/.bat supplied by a repository.
                if (!Path.IsPathFullyQualified(candidateDirectory)) continue;
                var candidate = Path.Combine(candidateDirectory, "git.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        throw new FileNotFoundException("Git is unavailable in the user's PATH.");
    }

    // Kept internal for native launcher verification; production callers supply only resolved
    // git.exe and the shared closed command allowlist above.
    internal static Result Run(SafeAccessTokenHandle primary, string executable, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken,
        int maximumOutputBytes = UserExecutionProtocol.MaximumResultBytes / 8)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commandLine = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument)));
        if (commandLine.Length >= 32767) throw new ArgumentException("Git command line is too long.");
        using var job = WindowsComponentJob.Create();
        using var stdout = new ChildPipe();
        using var stderr = new ChildPipe();
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        using var stdin = CreateFileW("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero);
        if (stdin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var environmentBlock = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(pair => pair.Key,
            StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
        var attributeList = IntPtr.Zero;
        var attributesInitialized = false;
        SafeFileHandle? process = null;
        Task<string>? outputTask = null, errorTask = null;
        var budget = new OutputBudget(maximumOutputBytes);
        try
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            attributesInitialized = true;
            Marshal.Copy(new[] { stdin.DangerousGetHandle(), stdout.WriteHandle.DangerousGetHandle(), stderr.WriteHandle.DangerousGetHandle() }, 0, handleList, 3);
            // Only these three handles may cross the process boundary, even during concurrent requests.
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)0x20002, handleList, (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = stdin.DangerousGetHandle(), Output = stdout.WriteHandle.DangerousGetHandle(), Error = stderr.WriteHandle.DangerousGetHandle() },
                AttributeList = attributeList,
            };
            // Explicit application path; no shell, PATH lookup or LocalSystem fallback. Suspended
            // creation closes the race in which Git could spawn a child before joining the job.
            if (!CreateProcessAsUserW(primary, executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    0x00080000 | 0x00000400 | 0x08000000 | 0x00000004,
                    environmentBlock, workingDirectory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = new SafeFileHandle(information.Process, true);
            using var thread = new SafeFileHandle(information.Thread, true);
            try
            {
                if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                cancellationToken.ThrowIfCancellationRequested();
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { TerminateProcess(process, 1); throw; }
            stdout.WriteHandle.Dispose();
            stderr.WriteHandle.Dispose();
            outputTask = stdout.ReadAsync(budget);
            errorTask = stderr.ReadAsync(budget);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.Exceeded) throw new OutputTooLargeException();
                var wait = WaitForSingleObject(process, 50);
                if (wait == 0) break;
                if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // Descendants cannot retain a pipe or live past this operation, even if Git exited first.
            job.Dispose();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Exceeded) throw new OutputTooLargeException();
            if (!GetExitCodeProcess(process, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(exitCode == 0, unchecked((int)exitCode), outputTask.Result, errorTask.Result);
        }
        finally
        {
            job.Dispose();
            stdout.WriteHandle.Dispose();
            stderr.WriteHandle.Dispose();
            // Drain pipes only after terminating the job, then release all native handles. No
            // cancellation registration or task may outlive the token/credential scope.
            try
            {
                if (outputTask is not null && errorTask is not null) Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            }
            finally
            {
                process?.Dispose();
                if (attributesInitialized) DeleteProcThreadAttributeList(attributeList);
                if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
                Marshal.FreeHGlobal(handleList);
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class OutputBudget(int limit)
    {
        private long _bytes;
        internal bool Exceeded => Volatile.Read(ref _bytes) > limit;
        internal bool Reserve(int bytes) => Interlocked.Add(ref _bytes, bytes) <= limit;
    }

    private sealed class ChildPipe : IDisposable
    {
        internal SafeFileHandle WriteHandle { get; }
        private readonly FileStream _reader;
        internal ChildPipe()
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out var read, out var write, ref attributes, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            WriteHandle = write;
            try
            {
                if (!SetHandleInformation(read, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _reader = new FileStream(read, FileAccess.Read);
            }
            catch { read.Dispose(); write.Dispose(); throw; }
        }
        internal async Task<string> ReadAsync(OutputBudget budget)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await _reader.ReadAsync(buffer);
                if (count == 0) break;
                if (budget.Reserve(count)) bytes.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        public void Dispose() { WriteHandle.Dispose(); _reader.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal short Show, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
