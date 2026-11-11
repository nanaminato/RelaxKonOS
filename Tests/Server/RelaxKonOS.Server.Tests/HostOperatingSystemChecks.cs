/// <summary>
/// Checks the host operating system classification behind `/server/host-operating-system`.
///
/// The endpoint is anonymous, so its worst failure is not an exception but a confident wrong answer: a
/// Windows Server that reports itself as Windows 11, or a Linux host wearing a distribution's mark it
/// never claimed. The checks below therefore pin the two things a client depends on — that the answer
/// agrees with the host this suite is running on, and that the wire value is the exact camelCase name
/// the clients parse.
/// </summary>
public static class HostOperatingSystemChecks
{
    private const string ExpectedRoute = "/api/v1.0/server/host-operating-system";

    public static void Run()
    {
        CheckRouteMatchesTheContract();
        CheckClassificationAgreesWithTheHostItRunsOn();
        CheckTheWireValueIsTheCamelCaseEnumName();
        CheckWindowsFactsAndPredicateAreOneAnswer();
        Console.WriteLine("Host operating system checks passed.");
    }

    private static void CheckRouteMatchesTheContract()
    {
        // The Android client hardcodes the same path; a rename on either side must fail here rather
        // than silently degrade into "the connection list never shows a platform mark again".
        Equal(ExpectedRoute, ServerApiRoutes.HostOperatingSystem, "The host operating system route");
    }

    private static void CheckClassificationAgreesWithTheHostItRunsOn()
    {
        var kind = HostOperatingSystemDescriptor.Classify();

        if (OperatingSystem.IsWindows())
        {
            // Every Windows host this project supports is either a 10/11 workstation or a Server SKU,
            // so `Unknown` here means `RtlGetVersion` itself failed — not an exotic host.
            if (WindowsWorkstationPlatform.IsWindows10Or11Workstation())
            {
                True(kind is HostOperatingSystemKind.Windows10 or HostOperatingSystemKind.Windows11,
                    $"A Windows 10/11 workstation is classified as such (actual={kind})");
            }
            else
            {
                Equal(HostOperatingSystemKind.WindowsServer, kind, "A non-workstation Windows host is a Server");
            }
            return;
        }

        True(kind is HostOperatingSystemKind.Ubuntu or HostOperatingSystemKind.Debian
                or HostOperatingSystemKind.Linux or HostOperatingSystemKind.Unknown,
            $"A non-Windows host is never reported as Windows (actual={kind})");
        if (!OperatingSystem.IsLinux()) return;

        // Read the distribution independently of the implementation. Every Linux host has an answer, so
        // a different one here — `Unknown` included — is the whole feature failing quietly.
        Equal(ExpectedLinuxKind(ReadOsReleaseId()), kind,
            "A Linux host reports the mark its distribution is");
    }

    /// <summary>A second reading of `/etc/os-release`, so this check has an answer of its own to compare.</summary>
    private static HostOperatingSystemKind ExpectedLinuxKind(string? id) => id switch
    {
        "ubuntu" => HostOperatingSystemKind.Ubuntu,
        "debian" => HostOperatingSystemKind.Debian,
        // Anything else is still Linux, and the mark may only claim that much.
        _ => HostOperatingSystemKind.Linux,
    };

    private static string? ReadOsReleaseId()
    {
        if (!File.Exists("/etc/os-release")) return null;
        foreach (var line in File.ReadAllLines("/etc/os-release"))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line.AsSpan(0, separator).Trim().Equals("ID", StringComparison.Ordinal)) continue;
            return line[(separator + 1)..].Trim().Trim('"', '\'').ToLowerInvariant();
        }
        return null;
    }

    private static void CheckTheWireValueIsTheCamelCaseEnumName()
    {
        // The clients parse this string case-insensitively but by name; a numeric value or a renamed
        // member would leave every row on the generic mark. The Linux marks are pinned one by one
        // because each of them needs a matching client mark to be worth answering.
        Equal("{\"kind\":\"windowsServer\"}", Payload(HostOperatingSystemKind.WindowsServer),
            "The Windows Server payload");
        Equal("{\"kind\":\"ubuntu\"}", Payload(HostOperatingSystemKind.Ubuntu), "The Ubuntu payload");
        Equal("{\"kind\":\"debian\"}", Payload(HostOperatingSystemKind.Debian), "The Debian payload");
        Equal("{\"kind\":\"linux\"}", Payload(HostOperatingSystemKind.Linux), "The generic Linux payload");
    }

    private static string Payload(HostOperatingSystemKind kind) =>
        JsonSerializer.Serialize(new HostOperatingSystemDto(kind), RelaxKonOSJsonOptions.Default);

    private static void CheckWindowsFactsAndPredicateAreOneAnswer()
    {
        var facts = WindowsWorkstationPlatform.Read();
        if (!OperatingSystem.IsWindows())
        {
            True(facts is null, "A non-Windows host has no RtlGetVersion facts");
            return;
        }

        True(facts is not null, "A Windows host can be asked for its version facts");
        var workstation = facts!.Value is
        {
            ProductType: WindowsVersionFacts.Workstation,
            MajorVersion: 10,
        } && facts.Value.BuildNumber >= WindowsWorkstationPlatform.Windows10MinimumBuild;
        Equal(workstation, WindowsWorkstationPlatform.IsWindows10Or11Workstation(),
            "The workstation predicate is the facts, not a second opinion");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message) => Assert(condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} (expected={expected}, actual={actual})");
    }
}
