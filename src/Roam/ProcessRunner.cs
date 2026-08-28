using System.Diagnostics;
using System.Text;

namespace Roam;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        ApplyProcessOptions(startInfo, workingDirectory, environment);
        return await ExecuteAsync(startInfo, cancellationToken);
    }

    public static async Task<ProcessResult> RunBashAsync(
        string script,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            // pwsh 7+ preserves bash-style single-quoted literals and supports && chaining,
            // which keeps PublishCommandBuilder's existing quoting working unchanged.
            startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
        }
        else
        {
            // Resolve bash via PATH rather than hardcoding /usr/bin/bash: on NixOS, Alpine, and
            // Homebrew-bash macOS the interpreter lives elsewhere (/bin/bash, /opt/homebrew/bin/bash,
            // /usr/local/bin/bash). With UseShellExecute=false, .NET searches PATH for a bare command
            // name on Unix, so "bash" finds whichever bash the controller actually has.
            startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(script);
        }

        ApplyProcessOptions(startInfo, workingDirectory, environment);
        return await ExecuteAsync(startInfo, cancellationToken);
    }

    // Runs a process with arguments passed as a literal argv list (ArgumentList), so no local shell
    // — bash OR pwsh — re-parses them. This is the SSH transport's path: the remote payload is one
    // argv entry that .NET <-> the OS arg-encoding delivers to `ssh` intact, and `ssh` forwards it to
    // the remote shell verbatim. Fixes the Windows-controller bug where a bash-quoted command string
    // was mis-parsed by pwsh (see SshHostResolver.BuildSshArgs).
    public static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyProcessOptions(startInfo, workingDirectory, environment);
        return await ExecuteAsync(startInfo, cancellationToken);
    }

    public static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'")}'";

    public static string PowerShellQuote(string value)
        => $"'{value.Replace("'", "''")}'";

    public static string BuildEnvironmentPrefix(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var builder = new StringBuilder();
        foreach (var pair in variables)
        {
            builder.Append(pair.Key)
                .Append('=')
                .Append(ShellQuote(pair.Value))
                .Append(' ');
        }

        return builder.ToString();
    }

    private static void ApplyProcessOptions(ProcessStartInfo startInfo, string? workingDirectory, IDictionary<string, string?>? environment)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environment is null)
        {
            return;
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private static async Task<ProcessResult> ExecuteAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        RoamLog.Event("process.start", "process starting", new Dictionary<string, object?>
        {
            ["fileName"] = startInfo.FileName,
            ["arguments"] = string.Join(" ", startInfo.ArgumentList),
            ["workingDirectory"] = startInfo.WorkingDirectory,
        });

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Preserve the cancellation as the caller-visible outcome.
            }

            throw;
        }
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        RoamLog.Event("process.end", "process exited", new Dictionary<string, object?>
        {
            ["fileName"] = startInfo.FileName,
            ["exitCode"] = process.ExitCode,
            ["elapsedMs"] = Environment.TickCount64 - started,
            ["stdoutBytes"] = Encoding.UTF8.GetByteCount(stdOut),
            ["stderrBytes"] = Encoding.UTF8.GetByteCount(stdErr),
            ["stdoutFirstLine"] = FirstLine(stdOut),
            ["stderrFirstLine"] = FirstLine(stdErr),
        });
        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
