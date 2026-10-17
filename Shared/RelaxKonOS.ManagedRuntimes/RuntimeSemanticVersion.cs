using System.Globalization;
using System.Numerics;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>Strict SemVer precedence, only for adapters that explicitly use this version scheme.</summary>
public sealed class RuntimeSemanticVersion : IComparable<RuntimeSemanticVersion>
{
    private readonly BigInteger[] core;
    private readonly string[] prerelease;
    public string Normalized { get; }
    public bool IsPrerelease => prerelease.Length != 0;
    private RuntimeSemanticVersion(string value, BigInteger[] core, string[] prerelease)
    { Normalized = value; this.core = core; this.prerelease = prerelease; }

    public static bool TryParse(string? value, out RuntimeSemanticVersion version)
    {
        version = null!;
        if (value is not { Length: > 0 and <= 128 }) return false;
        var metadata = value.Split('+');
        if (metadata.Length > 2 || metadata.Length == 2 && !Identifiers(metadata[1], numericLeadingZeroAllowed: true)) return false;
        var dash = metadata[0].IndexOf('-');
        var numbers = (dash < 0 ? metadata[0] : metadata[0][..dash]).Split('.');
        if (numbers.Length != 3 || numbers.Any(n => !Number(n))) return false;
        var preview = dash < 0 ? [] : metadata[0][(dash + 1)..].Split('.');
        if (dash >= 0 && !Identifiers(metadata[0][(dash + 1)..], numericLeadingZeroAllowed: false)) return false;
        version = new(value, numbers.Select(n => BigInteger.Parse(n, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray(), preview);
        return true;
    }

    public int CompareTo(RuntimeSemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var result = core[i].CompareTo(other.core[i]); if (result != 0) return result; }
        if (prerelease.Length == 0 || other.prerelease.Length == 0)
            return (prerelease.Length == 0).CompareTo(other.prerelease.Length == 0);
        for (var i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            var left = prerelease[i]; var right = other.prerelease[i];
            var leftNumber = left.All(char.IsAsciiDigit); var rightNumber = right.All(char.IsAsciiDigit);
            var result = leftNumber && rightNumber ? BigInteger.Parse(left, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(right, CultureInfo.InvariantCulture))
                : leftNumber != rightNumber ? rightNumber.CompareTo(leftNumber) : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    private static bool Number(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) && (value.Length == 1 || value[0] != '0');
    private static bool Identifiers(string value, bool numericLeadingZeroAllowed) => value.Split('.').All(part => part.Length > 0
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && (numericLeadingZeroAllowed || !part.All(char.IsAsciiDigit) || Number(part)));
}
