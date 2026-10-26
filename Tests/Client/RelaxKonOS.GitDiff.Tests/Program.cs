using RelaxKonOS.GitDiff.Tests;

// Patch parsing is pure logic and runs without a UI: it is the default so the fast check is the
// one you get by double-clicking the executable. `--render-only` starts the headless Skia renderer.
if (!args.Contains("--render-only"))
{
    GitCredentialChecks.Run();
    Console.WriteLine($"PASS: {DiffDocumentChecks.Run()} patch parsing checks.");
}

if (args.Contains("--render-only") || args.Contains("--all"))
{
    DiffRenderChecks.Run();
}
