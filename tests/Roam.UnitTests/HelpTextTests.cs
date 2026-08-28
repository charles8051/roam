using Xunit;

namespace Roam.UnitTests;

public sealed class HelpTextTests
{
    private static string ProjectPath()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        return Path.Combine(repositoryRoot, "src/Roam/Roam.csproj");
    }

    [Fact]
    public async Task TopLevelHelpMatchesContractPhrases()
    {
        var projectPath = ProjectPath();
        var result = await ProcessRunner.RunAsync("dotnet", $"run --project {projectPath} -- --help", Path.GetDirectoryName(projectPath)!);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("roam — build .NET on any host, run on any host, debug from anywhere.", result.StdOut);
        Assert.Contains("run <profile>", result.StdOut);
        Assert.Contains("deploy <profile>", result.StdOut);
        Assert.Contains("attach <profile>", result.StdOut);
    }

    // `roam deploy --help` renders the sync-only/register-without-start contract and the role
    // overrides, mirroring `roam run --help`.
    [Fact]
    public async Task DeployHelpRendersSyncOnlyContract()
    {
        var projectPath = ProjectPath();
        var result = await ProcessRunner.RunAsync("dotnet", $"run --project {projectPath} -- deploy --help", Path.GetDirectoryName(projectPath)!);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("roam deploy", result.StdOut);
        Assert.Contains("sync-source → publish → stop → sync-artifacts", result.StdOut);
        Assert.Contains("registers the Roam_<profile>", result.StdOut);
        Assert.Contains("does NOT start it", result.StdOut);
        Assert.Contains("--target <host>", result.StdOut);
    }

    // `roam deploy` with no <profile> is a usage error (exit 2), like `roam run`.
    [Fact]
    public async Task DeployWithoutProfileIsUsageError()
    {
        var projectPath = ProjectPath();
        var result = await ProcessRunner.RunAsync("dotnet", $"run --project {projectPath} -- deploy", Path.GetDirectoryName(projectPath)!);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("roam deploy requires exactly one <profile> argument", result.StdErr);
    }
}
