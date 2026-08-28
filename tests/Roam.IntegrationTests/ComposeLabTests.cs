using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Roam.IntegrationTests;

public sealed class ComposeLabTests
{
    private readonly ITestOutputHelper _output;

    public ComposeLabTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "ComposeLab")]
    public async Task ComposeLabRunnerPassesWhenExplicitlyEnabled()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var runLabScript = Path.Combine(repositoryRoot, "tests", "labs", "compose", "run-lab.sh");

        if (!string.Equals(Environment.GetEnvironmentVariable("ROAM_RUN_COMPOSE_LAB"), "1", StringComparison.Ordinal))
        {
            _output.WriteLine("Skipping Compose lab. Set ROAM_RUN_COMPOSE_LAB=1 to run tests/labs/compose/run-lab.sh.");
            return;
        }

        Assert.True(File.Exists(runLabScript), $"Compose lab runner was not found: {runLabScript}");

        var dockerCheck = await ProcessRunner.RunBashAsync("command -v docker >/dev/null && docker compose version >/dev/null", workingDirectory: repositoryRoot);
        Assert.True(
            dockerCheck.ExitCode == 0,
            "ROAM_RUN_COMPOSE_LAB=1 was set, but Docker Compose is unavailable. "
            + $"stdout: {dockerCheck.StdOut}\nstderr: {dockerCheck.StdErr}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var result = await ProcessRunner.RunBashAsync(
            $"bash {ProcessRunner.ShellQuote(runLabScript)}",
            workingDirectory: repositoryRoot,
            cancellationToken: timeout.Token);

        _output.WriteLine(result.StdOut);
        _output.WriteLine(result.StdErr);
        Assert.True(
            result.ExitCode == 0,
            $"Compose lab failed with exit code {result.ExitCode}.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
        Assert.Contains("[roam-lab] compose lab passed", result.StdOut);
    }
}
