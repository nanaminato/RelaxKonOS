using System.Diagnostics;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Installation-controlled environment for Helper and its privileged children; never the edited user environment.</summary>
public static class TrustedProcessEnvironment
{
    public static void Apply(ProcessStartInfo start)
    {
        if (!Path.IsPathFullyQualified(start.FileName)) throw new ArgumentException("A trusted absolute executable path is required.");
        start.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.SystemDirectory;
            var windows = Directory.GetParent(system)!.FullName;
            start.Environment["SystemRoot"] = windows;
            start.Environment["WINDIR"] = windows;
            start.Environment["PATH"] = system;
        }
        else
        {
            start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["HOME"] = "/root";
        }
    }
}
