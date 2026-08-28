using System.Text.Json;
using System.Text.Json.Nodes;

namespace Roam;

public static class DebuggerEmitter
{
    public static async Task EmitAsync(
        string outputPath,
        string profileName,
        string localSourceRoot,
        string localProjectDirectory,
        string remoteProjectDirectory,
        HostResolution targetHost,
        DebugSpec debug,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        JsonObject root;
        if (File.Exists(outputPath))
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(outputPath, cancellationToken)) as JsonObject
                ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var configurations = root["configurations"] as JsonArray ?? new JsonArray();
        root["version"] ??= "0.2.0";

        var retained = new JsonArray();
        foreach (var node in configurations)
        {
            if (node is not JsonObject existing)
            {
                continue;
            }

            var name = existing["name"]?.GetValue<string>();
            if (!string.Equals(name, $"roam: {profileName}", StringComparison.Ordinal))
            {
                // DeepClone detaches from the source array — JsonNode can have at most
                // one parent, and `existing` is still parented to the loaded `configurations`.
                retained.Add(existing.DeepClone());
            }
        }

        retained.Add(new JsonObject
        {
            ["name"] = $"roam: {profileName}",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processName"] = debug.ProcessName,
            ["justMyCode"] = false,
            ["pipeTransport"] = new JsonObject
            {
                ["pipeProgram"] = "ssh",
                ["pipeArgs"] = BuildPipeArgs(targetHost),
                ["debuggerPath"] = BuildDebuggerPath(targetHost),
                ["pipeCwd"] = NormalizePath(localSourceRoot),
                ["quoteArgs"] = true,
            },
            ["sourceFileMap"] = new JsonObject
            {
                [NormalizePath(remoteProjectDirectory)] = NormalizePath(localProjectDirectory),
            },
        });

        root["configurations"] = retained;

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, cancellationToken);
    }

    // pipeTransport runs ssh from the VSCode source host. Build a full arg list so
    // hosts on non-default ports / with explicit identity files / via ProxyJump all
    // connect. `-T` keeps OpenSSH from allocating a pseudo-tty (vsdbg speaks a
    // binary protocol over stdin/stdout and a tty mangles it).
    internal static JsonArray BuildPipeArgs(HostResolution targetHost)
    {
        var args = new JsonArray { "-T" };
        if (targetHost.Port != 22)
        {
            args.Add("-p");
            args.Add(targetHost.Port.ToString());
        }
        if (!string.IsNullOrWhiteSpace(targetHost.IdentityFile))
        {
            args.Add("-i");
            args.Add(targetHost.IdentityFile);
        }
        if (!string.IsNullOrWhiteSpace(targetHost.ProxyJump))
        {
            args.Add("-J");
            args.Add(targetHost.ProxyJump);
        }
        args.Add($"{targetHost.User}@{targetHost.SshHost}");
        return args;
    }

    // Microsoft's GetVsDbg bootstrap installs vsdbg under `$HOME/.vsdbg` on
    // Linux/macOS and under `%USERPROFILE%\.vsdbg` on Windows. Use an explicit
    // path on Windows because `~` doesn't expand in OpenSSH's default shell on
    // Windows (cmd.exe) — only pwsh / bash expand it. Linux/macOS keep the
    // tilde-relative form because the user's home directory may be at any of
    // /home/<user>, /Users/<user>, /var/<user>, or a chroot.
    internal static string BuildDebuggerPath(HostResolution targetHost)
        => string.Equals(targetHost.Os, "windows", StringComparison.OrdinalIgnoreCase)
            ? $"C:/Users/{targetHost.User}/.vsdbg/vsdbg.exe"
            : "~/.vsdbg/vsdbg";

    // VSCode launch.json prefers forward slashes for all paths (including
    // Windows paths). Mixing backslashes with JSON quoting also lands you in
    // escape-character territory. Normalize once at emit time.
    internal static string NormalizePath(string path)
        => path.Replace('\\', '/');
}
