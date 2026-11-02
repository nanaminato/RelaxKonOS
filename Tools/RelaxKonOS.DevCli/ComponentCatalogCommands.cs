using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.Catalog;

internal static class ComponentCatalogCommands
{
    internal static async Task<int> RunAsync(List<string> arguments)
    {
        try
        {
            if (arguments.Count == 0 || arguments[0] != "publish")
                throw new ArgumentException("component-catalog publish --root DIR --source-id ID --publisher ID --sequence N --key-id ID --key PRIVATE.pem --output catalog.signed.json [--revoke-id RELEASE_HASH] [--revoke-digest SHA256]");
            arguments.RemoveAt(0);
            string Require(string name)
            {
                var index = arguments.IndexOf(name);
                if (index < 0 || index + 1 >= arguments.Count) throw new ArgumentException("Missing " + name);
                var value = arguments[index + 1]; arguments.RemoveRange(index, 2); return value;
            }
            string[] Many(string name)
            {
                var values = new List<string>(); while (arguments.Contains(name)) values.Add(Require(name)); return values.ToArray();
            }
            var root = Require("--root"); var source = Require("--source-id"); var publisher = Require("--publisher");
            var sequence = long.Parse(Require("--sequence"), System.Globalization.CultureInfo.InvariantCulture);
            var keyId = Require("--key-id"); var keyPath = Require("--key"); var output = Require("--output");
            var revokedIds = Many("--revoke-id"); var revokedDigests = Many("--revoke-digest");
            if (arguments.Count != 0) throw new ArgumentException("Unknown component-catalog option");
            var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(keyPath).StartsWith(rootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("The private key must be outside the published catalog directory.");
            for (string? current = Path.GetFullPath(keyPath); current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("The signing key path cannot contain symbolic links or reparse points.");
            using var key = ECDsa.Create(); key.ImportFromPem(await File.ReadAllTextAsync(keyPath));
            var now = DateTimeOffset.UtcNow;
            var bytes = await ComponentCatalogPublisher.BuildAsync(root, source, publisher, sequence, keyId, key, now, now.AddDays(7), revokedIds, revokedDigests);
            await File.WriteAllBytesAsync(output, bytes);
            Console.WriteLine("Signed component catalog: " + Path.GetFullPath(output));
            Console.WriteLine("publicKeySpki=" + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            return 0;
        }
        catch (Exception error) when (error is RuntimePolicyException or IOException or ArgumentException or FormatException or CryptographicException)
        { Console.Error.WriteLine(error is RuntimePolicyException policy ? policy.ProblemCode : "Component catalog publishing failed: " + error.GetType().Name); return 1; }
    }
}
