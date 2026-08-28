using System.Text;
using YamlDotNet.RepresentationModel;

namespace Roam;

public static class ConfigLoader
{
    public static Roamfile Load(string roamfilePath)
    {
        if (!File.Exists(roamfilePath))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"roamfile.yaml not found at '{roamfilePath}'");
        }

        using var reader = new StreamReader(roamfilePath, Encoding.UTF8);
        var stream = new YamlStream();

        try
        {
            stream.Load(reader);
        }
        catch (Exception ex)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"failed to parse roamfile.yaml: {ex.Message}");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must contain a top-level mapping");
        }

        RequireOnlyKeys(root, ["version", "project", "solution", "csproj", "hosts", "profiles"], "top-level");

        var version = GetRequiredInt(root, "version");
        if (version != 1)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"roamfile version '{version}' is not supported; v0 requires 1");
        }

        var solution = GetOptionalString(root, "solution");
        var csproj = GetOptionalString(root, "csproj");

        if (string.IsNullOrWhiteSpace(solution) == string.IsNullOrWhiteSpace(csproj))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must set exactly one of 'solution' or 'csproj'");
        }

        var hostsNode = GetRequiredMapping(root, "hosts");
        var profilesNode = GetRequiredMapping(root, "profiles");

        var hosts = new Dictionary<string, HostSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in hostsNode.Children)
        {
            var name = RequireScalarKey(child.Key, "host name");
            if (child.Value is not YamlMappingNode hostNode)
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"host '{name}' must be a mapping");
            }

            RequireOnlyKeys(hostNode, ["ssh", "user", "port", "identity-file", "workspace", "os"], $"hosts.{name}");
            var os = GetOptionalString(hostNode, "os");
            if (os is not null && os is not ("linux" or "macos" or "windows"))
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"host '{name}' has unsupported os '{os}'; roam accepts only linux, macos, and windows");
            }

            hosts[name] = new HostSpec(
                GetOptionalString(hostNode, "ssh"),
                GetOptionalString(hostNode, "user"),
                GetOptionalInt(hostNode, "port"),
                GetOptionalString(hostNode, "identity-file"),
                GetOptionalString(hostNode, "workspace"),
                os);
        }

        if (hosts.Count == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must define at least one host");
        }

        var profiles = new Dictionary<string, ProfileSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in profilesNode.Children)
        {
            var name = RequireScalarKey(child.Key, "profile name");
            if (child.Value is not YamlMappingNode profileNode)
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{name}' must be a mapping");
            }

            RequireOnlyKeys(profileNode, ["description", "source", "build", "target", "publish-profile", "publish", "launch-profile", "env", "deploy", "run", "debug"], $"profiles.{name}");

            var publishProfile = GetOptionalString(profileNode, "publish-profile");
            var publishNode = GetOptionalMapping(profileNode, "publish");
            if (string.IsNullOrWhiteSpace(publishProfile) == (publishNode is null))
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{name}' must set exactly one of 'publish-profile' or 'publish'");
            }

            var env = GetOptionalStringMap(profileNode, "env");
            var deployNode = GetRequiredMapping(profileNode, "deploy");
            RequireOnlyKeys(deployNode, ["path", "flatten-publish", "stop", "start", "ready", "ready-timeout", "ready-interval-ms", "interactive-session", "interactive-session-trigger", "run-level", "detach", "transfer", "uninstall", "diag", "provenance"], $"profiles.{name}.deploy");

            var runNode = GetOptionalMapping(profileNode, "run");
            if (runNode is not null)
            {
                RequireOnlyKeys(runNode, ["mode", "command", "stop", "ready", "ready-timeout", "ready-interval-ms", "interactive-session", "interactive-session-trigger", "run-level", "detach", "timeout", "success-exit-codes"], $"profiles.{name}.run");
            }

            var debugNode = GetOptionalMapping(profileNode, "debug");
            if (debugNode is not null)
            {
                RequireOnlyKeys(debugNode, ["enabled", "debugger", "editor", "process-name", "install-on-target"], $"profiles.{name}.debug");
            }

            var debug = new DebugSpec(
                GetOptionalBool(debugNode, "enabled") ?? false,
                GetOptionalString(debugNode, "debugger"),
                GetOptionalString(debugNode, "editor"),
                GetOptionalString(debugNode, "process-name"),
                GetOptionalBool(debugNode, "install-on-target") ?? false);

            PublishSpec? publish = null;
            if (publishNode is not null)
            {
                RequireOnlyKeys(publishNode, ["rid", "self-contained", "configuration", "framework"], $"profiles.{name}.publish");
                publish = new PublishSpec(
                    GetRequiredString(publishNode, "rid"),
                    GetOptionalBool(publishNode, "self-contained") ?? true,
                    GetOptionalString(publishNode, "configuration"),
                    GetOptionalString(publishNode, "framework"));
            }

            ValidateDebugBlock(name, debug);

            DiagSpec? diag = null;
            var diagNode = GetOptionalMapping(deployNode, "diag");
            if (diagNode is not null)
            {
                RequireOnlyKeys(diagNode, ["crash-dumps", "logs", "unit", "tool-source", "dump-type"], $"profiles.{name}.deploy.diag");
                diag = new DiagSpec(
                    GetOptionalBool(diagNode, "crash-dumps") ?? false,
                    GetOptionalStringList(diagNode, "logs") ?? [],
                    GetOptionalString(diagNode, "unit"),
                    ParseDiagToolSource(name, GetOptionalString(diagNode, "tool-source")),
                    GetOptionalInt(diagNode, "dump-type") ?? 2);
            }

            var deploy = new DeploySpec(
                GetRequiredString(deployNode, "path"),
                GetOptionalBool(deployNode, "flatten-publish") ?? false,
                GetOptionalString(deployNode, "stop"),
                GetOptionalString(deployNode, "start"),
                GetOptionalString(deployNode, "ready"),
                GetOptionalInt(deployNode, "ready-timeout") ?? 15,
                GetOptionalInt(deployNode, "ready-interval-ms") ?? 500,
                GetOptionalBool(deployNode, "interactive-session") ?? false,
                ParseTransferMode(name, GetOptionalString(deployNode, "transfer")),
                GetOptionalString(deployNode, "uninstall"),
                ParseInteractiveSessionTrigger(name, GetOptionalString(deployNode, "interactive-session-trigger")),
                ParseRunLevel(name, GetOptionalString(deployNode, "run-level")),
                GetOptionalBool(deployNode, "detach") ?? false,
                diag,
                GetOptionalStringList(deployNode, "provenance"));
            var run = ParseRunSpec(name, runNode, deploy);

            profiles[name] = new ProfileSpec(
                GetOptionalString(profileNode, "description"),
                GetRequiredString(profileNode, "source"),
                GetRequiredString(profileNode, "build"),
                GetRequiredString(profileNode, "target"),
                publishProfile,
                publish,
                GetRequiredString(profileNode, "launch-profile"),
                env,
                deploy,
                run,
                debug);
        }

        if (profiles.Count == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must define at least one profile");
        }

        return new Roamfile(version, GetOptionalString(root, "project"), solution, csproj, hosts, profiles);
    }

    public static string Discover(string? explicitPath, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath, workingDirectory);
        }

        var current = new DirectoryInfo(workingDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "roamfile.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new RoamException(ExitCode.Config, "parse", "local", "could not find roamfile.yaml by walking up from the current directory");
    }

    private static SyncTransferMode ParseTransferMode(string profileName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SyncTransferMode.PerFile;
        }

        return value switch
        {
            "per-file" => SyncTransferMode.PerFile,
            "archive" => SyncTransferMode.Archive,
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has deploy.transfer '{value}'; expected 'per-file' or 'archive'"),
        };
    }

    private static InteractiveSessionTrigger ParseInteractiveSessionTrigger(string profileName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return InteractiveSessionTrigger.None;
        }

        return value switch
        {
            "at-logon" => InteractiveSessionTrigger.AtLogon,
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has interactive-session-trigger '{value}'; expected 'at-logon'"),
        };
    }

    private static RunLevel ParseRunLevel(string profileName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RunLevel.Limited;
        }

        return value switch
        {
            "limited" => RunLevel.Limited,
            "highest" => RunLevel.Highest,
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has run-level '{value}'; expected 'limited' or 'highest'"),
        };
    }

    private static RunSpec ParseRunSpec(string profileName, YamlMappingNode? runNode, DeploySpec deploy)
    {
        if (runNode is null)
        {
            return new RunSpec(
                RunMode.Service,
                deploy.Start,
                deploy.Stop,
                deploy.Ready,
                deploy.ReadyTimeoutSeconds,
                deploy.ReadyIntervalMilliseconds,
                deploy.InteractiveSession,
                60,
                [0],
                deploy.InteractiveSessionTrigger,
                deploy.RunLevel,
                deploy.Detach);
        }

        var mode = ParseRunMode(profileName, GetOptionalString(runNode, "mode"));
        var command = GetOptionalString(runNode, "command");
        if (mode == RunMode.OneShot && string.IsNullOrWhiteSpace(command))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has run.mode 'one-shot' but does not set run.command");
        }

        return new RunSpec(
            mode,
            command,
            GetOptionalString(runNode, "stop"),
            GetOptionalString(runNode, "ready"),
            GetOptionalInt(runNode, "ready-timeout") ?? deploy.ReadyTimeoutSeconds,
            GetOptionalInt(runNode, "ready-interval-ms") ?? deploy.ReadyIntervalMilliseconds,
            GetOptionalBool(runNode, "interactive-session") ?? false,
            GetOptionalInt(runNode, "timeout") ?? 60,
            GetOptionalIntList(runNode, "success-exit-codes") ?? [0],
            ParseInteractiveSessionTrigger(profileName, GetOptionalString(runNode, "interactive-session-trigger")),
            ParseRunLevel(profileName, GetOptionalString(runNode, "run-level")),
            GetOptionalBool(runNode, "detach") ?? false);
    }

    private static RunMode ParseRunMode(string profileName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RunMode.Service;
        }

        return value switch
        {
            "service" => RunMode.Service,
            "one-shot" => RunMode.OneShot,
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has run.mode '{value}'; expected 'service' or 'one-shot'"),
        };
    }

    private static void ValidateDebugBlock(string profileName, DebugSpec debug)
    {
        if (debug.Editor is not null && !string.Equals(debug.Editor, "vscode", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' uses debug.editor='{debug.Editor}'; v0 only supports 'vscode'");
        }

        if (debug.Debugger is not null && !string.Equals(debug.Debugger, "vsdbg", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' uses debug.debugger='{debug.Debugger}'; v0 only supports 'vsdbg'");
        }

        if (debug.InstallOnTarget)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' sets debug.install-on-target=true; v0 only supports false");
        }
    }

    private static void RequireOnlyKeys(YamlMappingNode node, IReadOnlyCollection<string> allowedKeys, string path)
    {
        foreach (var child in node.Children)
        {
            var key = RequireScalarKey(child.Key, path);
            if (!allowedKeys.Contains(key))
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"unknown key '{key}' under {path}");
            }
        }
    }

    private static string RequireScalarKey(YamlNode node, string path)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"invalid key under {path}");
        }

        return scalar.Value;
    }

    private static YamlMappingNode GetRequiredMapping(YamlMappingNode node, string key)
        => GetOptionalMapping(node, key) ?? throw new RoamException(ExitCode.Config, "parse", "local", $"missing required mapping '{key}'");

    private static YamlMappingNode? GetOptionalMapping(YamlMappingNode? node, string key)
    {
        if (node is null)
        {
            return null;
        }

        if (!TryGetNode(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlMappingNode mapping)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be a mapping");
        }

        return mapping;
    }

    private static string GetRequiredString(YamlMappingNode node, string key)
        => GetOptionalString(node, key) ?? throw new RoamException(ExitCode.Config, "parse", "local", $"missing required value '{key}'");

    private static string? GetOptionalString(YamlMappingNode? node, string key)
    {
        if (node is null || !TryGetNode(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlScalarNode scalar)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be a scalar string");
        }

        return scalar.Value;
    }

    private static int GetRequiredInt(YamlMappingNode node, string key)
        => GetOptionalInt(node, key) ?? throw new RoamException(ExitCode.Config, "parse", "local", $"missing required integer '{key}'");

    private static int? GetOptionalInt(YamlMappingNode? node, string key)
    {
        var text = GetOptionalString(node, key);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out var value))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be an integer");
        }

        return value;
    }

    private static IReadOnlyList<int>? GetOptionalIntList(YamlMappingNode? node, string key)
    {
        if (node is null || !TryGetNode(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlSequenceNode sequence)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be an integer sequence");
        }

        var result = new List<int>();
        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode scalar || !int.TryParse(scalar.Value, out var parsed))
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must contain only integers");
            }

            result.Add(parsed);
        }

        return result;
    }

    private static IReadOnlyList<string>? GetOptionalStringList(YamlMappingNode? node, string key)
    {
        if (node is null || !TryGetNode(node, key, out var value))
        {
            return null;
        }

        if (value is not YamlSequenceNode sequence)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be a string sequence");
        }

        var result = new List<string>();
        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode scalar || scalar.Value is null)
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must contain only strings");
            }

            result.Add(scalar.Value);
        }

        return result;
    }

    private static DiagToolSource ParseDiagToolSource(string profileName, string? value)
        => value?.ToLowerInvariant() switch
        {
            null or "target" => DiagToolSource.Target,
            "bundled" => DiagToolSource.Bundled,
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' has deploy.diag.tool-source '{value}'; expected 'target' or 'bundled'"),
        };

    private static bool? GetOptionalBool(YamlMappingNode? node, string key)
    {
        var text = GetOptionalString(node, key);
        if (text is null)
        {
            return null;
        }

        if (!bool.TryParse(text, out var value))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}' must be a boolean");
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> GetOptionalStringMap(YamlMappingNode node, string key)
    {
        var mapping = GetOptionalMapping(node, key);
        if (mapping is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in mapping.Children)
        {
            var mapKey = RequireScalarKey(child.Key, key);
            if (child.Value is not YamlScalarNode scalar || scalar.Value is null)
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"'{key}.{mapKey}' must be a scalar string");
            }

            result[mapKey] = scalar.Value;
        }

        return result;
    }

    private static bool TryGetNode(YamlMappingNode node, string key, out YamlNode value)
    {
        foreach (var child in node.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = child.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }
}

public sealed class RoamException : Exception
{
    public RoamException(ExitCode exitCode, string step, string host, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        Step = step;
        Host = host;
    }

    public ExitCode ExitCode { get; }

    public string Step { get; }

    public string Host { get; }
}
