using System.Text;

namespace Roam;


public sealed class SshHostResolver
{
    public async Task<HostResolution> ResolveAsync(string hostName, HostSpec spec, bool isLocal, CancellationToken cancellationToken)
    {
        SshConfigSnapshot snapshot = new(null, null, null, null, [], null);
        var canUseSsh = File.Exists("/usr/bin/ssh") || File.Exists("/bin/ssh") || await HasCommandAsync("ssh", cancellationToken);

        if (canUseSsh)
        {
            var target = spec.Ssh ?? hostName;
            // RunAsync passes arguments via ProcessStartInfo, NOT through a shell, so the target must
            // not be shell-quoted: .NET's argv parser treats single quotes literally (a shell would
            // strip them) and Windows hands the string to ssh.exe verbatim. ShellQuote here made ssh
            // see the hostname as 'localhost' (quotes included) -> "hostname contains invalid
            // characters", fatal on a Linux controller for any host without an explicit user:.
            // SSH hostnames / IPs / config aliases carry no whitespace, so a bare arg is correct.
            var result = await ProcessRunner.RunAsync("ssh", $"-G {target}", cancellationToken: cancellationToken);
            if (result.ExitCode == 0)
            {
                snapshot = ParseSshConfig(result.StdOut);
            }
            else if (string.IsNullOrWhiteSpace(spec.Ssh) || string.IsNullOrWhiteSpace(spec.User))
            {
                throw new RoamException(ExitCode.Preflight, "preflight", hostName, $"ssh -G {target} failed: {FirstMeaningfulLine(result.StdErr) ?? FirstMeaningfulLine(result.StdOut) ?? "resolution failed"}");
            }
        }
        else if (string.IsNullOrWhiteSpace(spec.Ssh) || string.IsNullOrWhiteSpace(spec.User))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", hostName, $"ssh not found on PATH; host '{hostName}' requires explicit 'ssh:' and 'user:' in roamfile.yaml");
        }

        var sshHost = spec.Ssh ?? snapshot.HostName;
        var user = spec.User ?? snapshot.User;
        var port = spec.Port ?? snapshot.Port ?? 22;
        var identityFile = spec.IdentityFile ?? snapshot.IdentityFile;
        var identityFiles = spec.IdentityFile is not null
            ? PrependUnique(spec.IdentityFile, snapshot.IdentityFiles)
            : snapshot.IdentityFiles;
        var proxyJump = snapshot.ProxyJump;

        if (string.IsNullOrWhiteSpace(sshHost) || string.IsNullOrWhiteSpace(user))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", hostName, $"host '{hostName}' could not be resolved to a hostname/user tuple");
        }

        return new HostResolution(hostName, sshHost, user, port, identityFile, identityFiles, proxyJump, spec.Workspace, spec.Os, isLocal);
    }

    // The argv for an `ssh` invocation, passed to ProcessRunner.RunProcessAsync so no local shell
    // re-quotes the remote payload. Each entry is a literal argument; the remote command is the final
    // entry and reaches the remote shell verbatim (the controller shell never parses it). This is the
    // fix for the Windows-controller transport bug: the old bash-quoted command string was executed
    // via pwsh, which mis-parses bash's nested-quote (`'"'"'`) escaping and mangled env-prefixed /
    // detached commands.
    public IReadOnlyList<string> BuildSshArgs(HostResolution host, string remoteCommand)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-p", host.Port.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(host.IdentityFile))
        {
            args.Add("-i");
            args.Add(host.IdentityFile!);
        }

        if (!string.IsNullOrWhiteSpace(host.ProxyJump))
        {
            args.Add("-J");
            args.Add(host.ProxyJump!);
        }

        args.Add($"{host.User}@{host.SshHost}");
        args.Add(string.Equals(host.Os, "windows", StringComparison.OrdinalIgnoreCase)
            ? BuildWindowsRemoteCommand(remoteCommand)
            : remoteCommand);
        return args;
    }

    // Human-readable, loggable bash-quoted form of the ssh invocation, derived from BuildSshArgs.
    // NOT used for execution (that goes through BuildSshArgs + argv) — retained for diagnostics and
    // tests.
    public string BuildSshCommand(HostResolution host, string remoteCommand)
        => "ssh " + string.Join(' ', BuildSshArgs(host, remoteCommand).Select(ProcessRunner.ShellQuote));

    public string BuildScpToRemoteCommand(HostResolution host, string localPath, string remotePath, bool recursive = false)
    {
        var parts = new List<string>
        {
            "scp",
            "-q",
            "-P", host.Port.ToString(),
        };

        if (recursive)
        {
            parts.Add("-r");
        }

        if (!string.IsNullOrWhiteSpace(host.IdentityFile))
        {
            parts.Add("-i");
            parts.Add(ProcessRunner.ShellQuote(host.IdentityFile!));
        }

        if (!string.IsNullOrWhiteSpace(host.ProxyJump))
        {
            parts.Add("-o");
            parts.Add(ProcessRunner.ShellQuote($"ProxyJump={host.ProxyJump}"));
        }

        parts.Add(ProcessRunner.ShellQuote(localPath));
        parts.Add(ProcessRunner.ShellQuote($"{host.User}@{host.SshHost}:{remotePath}"));
        return string.Join(' ', parts);
    }

    public string BuildScpFromRemoteCommand(HostResolution host, string remotePath, string localPath, bool recursive)
    {
        var parts = new List<string>
        {
            "scp",
            "-q",
        };

        if (recursive)
        {
            parts.Add("-r");
        }

        parts.AddRange(["-P", host.Port.ToString()]);

        if (!string.IsNullOrWhiteSpace(host.IdentityFile))
        {
            parts.Add("-i");
            parts.Add(ProcessRunner.ShellQuote(host.IdentityFile!));
        }

        if (!string.IsNullOrWhiteSpace(host.ProxyJump))
        {
            parts.Add("-o");
            parts.Add(ProcessRunner.ShellQuote($"ProxyJump={host.ProxyJump}"));
        }

        parts.Add(ProcessRunner.ShellQuote($"{host.User}@{host.SshHost}:{remotePath}"));
        parts.Add(ProcessRunner.ShellQuote(localPath));
        return string.Join(' ', parts);
    }

    private static string BuildWindowsRemoteCommand(string script)
    {
        var wrappedScript = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "try {",
            script,
            "    exit 0",
            "}",
            "catch {",
            "    Write-Error $_",
            "    exit 1",
            "}");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));
        return $"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static SshConfigSnapshot ParseSshConfig(string output)
    {
        string? hostname = null;
        string? user = null;
        int? port = null;
        string? identityFile = null;
        var identityFiles = new List<string>();
        string? proxyJump = null;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = rawLine.IndexOf(' ');
            if (index <= 0)
            {
                continue;
            }

            var key = rawLine[..index];
            var value = rawLine[(index + 1)..].Trim();
            switch (key)
            {
                case "hostname":
                    hostname = value;
                    break;
                case "user":
                    user = value;
                    break;
                case "port" when int.TryParse(value, out var parsedPort):
                    port = parsedPort;
                    break;
                case "identityfile":
                    identityFile ??= value;
                    identityFiles.Add(value);
                    break;
                case "proxyjump":
                    proxyJump = value;
                    break;
            }
        }

        return new SshConfigSnapshot(hostname, user, port, identityFile, identityFiles, proxyJump);
    }

    private static IReadOnlyList<string> PrependUnique(string first, IReadOnlyList<string> rest)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(first) && seen.Add(first))
        {
            result.Add(first);
        }

        foreach (var value in rest)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static async Task<bool> HasCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var probe = await ProcessRunner.RunAsync("where", command, cancellationToken: cancellationToken);
            return probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut);
        }

        var result = await ProcessRunner.RunAsync("/usr/bin/env", $"bash -lc {ProcessRunner.ShellQuote($"command -v {command}")}", cancellationToken: cancellationToken);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    }

    // Delegates to the shared pure core so `ssh -G` resolution failures skip benign warnings
    // (charles8051/roam#7).
    private static string? FirstMeaningfulLine(string text)
        => SshOutputLines.FirstMeaningful(text);
}
