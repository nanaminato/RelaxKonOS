using RelaxKonOS.Server.Endpoints;
namespace RelaxKonOS.Server.Git;

public sealed class GitBuildOptions
{
    public string RootDirectory { get; set; } = "data/git-builds";
    public string BuilderName { get; set; } = "";
    public string BuilderContainer { get; set; } = "";
    public string BuilderNetwork { get; set; } = "";
    public string[] AllowedHosts { get; set; } = ["github.com", "gitlab.com"];
    public long MaximumMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public double MaximumCpuCores { get; set; } = 2;
    public int MaximumPids { get; set; } = 512;
    public int TimeoutMinutes { get; set; } = 20;
}

public sealed class GitBuildProblem(string code, int status = 409) : Exception(code), IProblemException
{
    public string Code { get; } = code;
    public int Status { get; } = status;

    string IProblemException.ProblemCode => Code;
    int IProblemException.StatusCode => Status;
}

