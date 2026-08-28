using System.Text;

namespace Roam;

public static class PublishCommandBuilder
{
    public static string Build(ResolvedProjectPaths paths, ResolvedPublishSettings publish, string? buildWorkspace, bool ciBuild)
    {
        var relativeProject = Path.GetRelativePath(paths.WorkspaceRoot, paths.ProjectPath).Replace('\\', '/');
        var command = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(buildWorkspace))
        {
            // `cd <ws> && dotnet ...`. The cd is load-bearing: `dotnet` selects the SDK from the
            // global.json found by walking up from the *current directory*, so the publish must run
            // inside the synced workspace. `&&` is honored by every shell roam runs publish through
            // today: local bash, local pwsh 7 (ProcessRunner.RunBashAsync), and a remote *Linux*
            // build host's login shell. The one shell that rejects `&&` is Windows PowerShell 5.1,
            // which only executes this string for a REMOTE Windows build host (source != build, build
            // os=windows) via SshHostResolver.BuildWindowsRemoteCommand. That topology is untested and
            // outside the proven matrix (all proven builds are local); emitting a PS-safe separator
            // for it is tracked in the issue tracker.
            command.Append("cd ").Append(ProcessRunner.ShellQuote(buildWorkspace)).Append(" && ");
        }

        command.Append("dotnet publish ")
            .Append(ProcessRunner.ShellQuote(relativeProject));

        if (publish.UsePublishProfile)
        {
            command.Append(" -p:PublishProfile=")
                .Append(ProcessRunner.ShellQuote(publish.Name!));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(publish.Configuration))
            {
                command.Append(" --configuration ")
                    .Append(ProcessRunner.ShellQuote(publish.Configuration));
            }

            if (!string.IsNullOrWhiteSpace(publish.RuntimeIdentifier))
            {
                command.Append(" --runtime ")
                    .Append(ProcessRunner.ShellQuote(publish.RuntimeIdentifier));
            }

            command.Append(" --self-contained ")
                .Append(publish.SelfContained ? "true" : "false");

            if (!string.IsNullOrWhiteSpace(publish.TargetFramework))
            {
                command.Append(" --framework ")
                    .Append(ProcessRunner.ShellQuote(publish.TargetFramework));
            }

            var relativeOutput = Path.GetRelativePath(paths.WorkspaceRoot, Path.Combine(paths.ProjectDirectory, publish.PublishDirectory)).Replace('\\', '/');
            command.Append(" --output ")
                .Append(ProcessRunner.ShellQuote(relativeOutput));
        }

        if (ciBuild)
        {
            command.Append(" -p:ContinuousIntegrationBuild=true");
        }

        command.Append(" --disable-build-servers");

        return command.ToString();
    }
}
