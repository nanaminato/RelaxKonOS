namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>
/// Debian version ordering. It exists for one purpose: binding the declared package version interval of a
/// module capability to the exact apt version the Host resolved. apt versions such as
/// <c>8.0.39-0ubuntu0.24.04.1</c> are not <see cref="System.Version"/> values, so the declaration is
/// compared with the same epoch/upstream/revision rules dpkg publishes instead of guessing a prefix.
/// </summary>
public static class DebianVersion
{
    /// <summary>Debian versions are printable, contain no whitespace and are at most 256 characters.</summary>
    public static bool IsUsable(string? value) => value is { Length: > 0 and <= 256 }
        && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace);

    /// <summary>True only when <paramref name="version"/> satisfies minimum ≤ version &lt; maximumExclusive.</summary>
    public static bool IsWithin(string version, string minimum, string maximumExclusive) =>
        IsUsable(version) && IsUsable(minimum) && IsUsable(maximumExclusive)
        && Compare(version, minimum) >= 0 && Compare(version, maximumExclusive) < 0;

    /// <summary>Same total order as <c>dpkg --compare-versions</c>; unusable input compares as unequal but
    /// never throws, and callers validate with <see cref="IsUsable"/> before trusting a result.</summary>
    public static int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (leftEpoch, leftUpstream, leftRevision) = Split(left);
        var (rightEpoch, rightUpstream, rightRevision) = Split(right);
        var epoch = leftEpoch.CompareTo(rightEpoch);
        if (epoch != 0) return epoch;
        var upstream = CompareParts(leftUpstream, rightUpstream);
        return upstream != 0 ? upstream : CompareParts(leftRevision, rightRevision);
    }

    private static (ulong Epoch, string Upstream, string Revision) Split(string value)
    {
        var rest = value;
        var epoch = 0UL;
        var colon = value.IndexOf(':');
        if (colon > 0 && value[..colon].All(char.IsAsciiDigit)
            && ulong.TryParse(value.AsSpan(0, colon), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            epoch = parsed;
            rest = value[(colon + 1)..];
        }
        var dash = rest.LastIndexOf('-');
        return dash >= 0 ? (epoch, rest[..dash], rest[(dash + 1)..]) : (epoch, rest, "");
    }

    /// <summary>The dpkg <c>verrevcmp</c> loop: non-digits by the dpkg character order, then digit runs by
    /// numeric value with leading zeros ignored.</summary>
    private static int CompareParts(string left, string right)
    {
        var i = 0;
        var j = 0;
        while (i < left.Length || j < right.Length)
        {
            while (i < left.Length && !char.IsAsciiDigit(left[i]) || j < right.Length && !char.IsAsciiDigit(right[j]))
            {
                var a = i < left.Length ? Order(left[i]) : 0;
                var b = j < right.Length ? Order(right[j]) : 0;
                if (a != b) return a.CompareTo(b);
                i++;
                j++;
            }
            while (i < left.Length && left[i] == '0') i++;
            while (j < right.Length && right[j] == '0') j++;
            var firstDifference = 0;
            while (i < left.Length && j < right.Length && char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                if (firstDifference == 0) firstDifference = left[i].CompareTo(right[j]);
                i++;
                j++;
            }
            // A remaining digit run is numerically larger than a shorter or absent one.
            if (i < left.Length && char.IsAsciiDigit(left[i])) return 1;
            if (j < right.Length && char.IsAsciiDigit(right[j])) return -1;
            if (firstDifference != 0) return firstDifference;
        }
        return 0;
    }

    /// <summary>dpkg's <c>order()</c>: '~' sorts before end-of-string, letters by their ASCII value and every
    /// other character after letters.</summary>
    private static int Order(char value) => char.IsAsciiDigit(value) ? 0
        : char.IsAsciiLetter(value) ? value
        : value == '~' ? -1
        : value + 256;
}
