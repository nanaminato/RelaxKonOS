using RelaxKonOS.Client.Apps.Git;

namespace RelaxKonOS.GitDiff.Tests;

internal static class GitCredentialChecks
{
    public static void Run()
    {
        var resolver = new GitCredentialResolver();
        resolver.AddOrUpdate(new("GitHub.com", "first", "a", true));
        resolver.AddOrUpdate(new("github.com", "second", "b", true));
        resolver.AddOrUpdate(new("gitee.com", "first", "c", true));
        resolver.AddOrUpdate(new("git.example.com:8443", "self", "d", true));
        Check(resolver.Resolve("https://github.com/org/repo.git")?.Username == "second");
        Check(resolver.Accounts.Count(x => x.Host == "github.com" && x.IsDefault) == 1);
        Check(resolver.Resolve("https://first@github.com/org/repo.git")?.Secret == "a");
        Check(resolver.Resolve("https://github.com/org/repo.git", "missing") is null);
        Check(resolver.Resolve("https://gitee.com/org/repo.git")?.Secret == "c");
        Check(resolver.Resolve("https://git.example.com:8443/org/repo")?.Secret == "d");
        Check(resolver.Resolve("https://git.example.com/org/repo") is null);
        Check(resolver.Resolve("ssh://git@github.com/org/repo") is null);
        Check(resolver.Resolve("git@github.com:org/repo") is null);
        Check(resolver.Resolve("http://github.com/org/repo") is null);
        Check(resolver.Resolve("https://github.com/org/repo")?.ToRequest().SaveCredentials == false);
        Check(resolver.Remove("GITHUB.COM", "second"));
        Check(resolver.Resolve("https://github.com/org/repo")?.Username == "first");
        Console.WriteLine("PASS: multi-host Git credential checks.");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Git credential regression failed.");
    }
}
