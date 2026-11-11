namespace RelaxKonOS.Server.Docker;

internal static class DockerImageReference
{
    public static bool IsValid(string? value) => value is { Length: >= 1 and <= 255 }
        && (HasSha256Digest(value) || IsName(value));

    public static bool HasSha256Digest(string? value) => value is { Length: > 72 and <= 255 }
        && value.AsSpan(value.Length - 72, 8).SequenceEqual("@sha256:")
        && IsName(value[..^72])
        && value[^64..].All(char.IsAsciiHexDigitLower);

    private static bool IsName(string value) => char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '.' or '_' or '-');
}
