using System.Runtime.InteropServices;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Roam;

public static class ConfigLoader
{
    // The reserved name for "the machine roam is running on". Synthesized when the roamfile omits
    // `hosts:` entirely, so a single-machine project needs no host block at all.
    private const string LocalHostName = "local";

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

        var version = GetOptionalInt(root, "version") ?? 1;
        if (version != 1)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"roamfile version '{version}' is not supported; v0 requires 1");
        }

        var workspaceRoot = Path.GetDirectoryName(Path.GetFullPath(roamfilePath))
            ?? throw new RoamException(ExitCode.Config, "parse", "local", $"could not determine the directory containing '{roamfilePath}'");

        var solution = GetOptionalString(root, "solution");
        var csproj = GetOptionalString(root, "csproj");

        if (!string.IsNullOrWhiteSpace(solution) && !string.IsNullOrWhiteSpace(csproj))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must set at most one of 'solution' or 'csproj'");
        }

        // Neither set: discover the project the way `roam init` does, so a single-project repo
        // needs no `csproj:` line. Ambiguity is an error that names the candidates.
        if (string.IsNullOrWhiteSpace(solution) && string.IsNullOrWhiteSpace(csproj))
        {
            csproj = DiscoverCsproj(workspaceRoot);
        }

        var project = GetOptionalString(root, "project");
        var projectName = ResolveProjectNameForDefaults(workspaceRoot, csproj, project);

        var hostsNode = GetOptionalMapping(root, "hosts");
        var profilesNode = GetRequiredMapping(root, "profiles");

        var hosts = new Dictionary<string, HostSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in (hostsNode ?? EmptyMapping()).Children)
        {
            var name = RequireScalarKey(child.Key, "host name");
            var hostNode = AsMapping(child.Value, $"host '{name}'");

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

        // No hosts at all: synthesize the reserved `local` host so a single-machine project can
        // omit the whole block. Everything it carries is derivable from the controller.
        if (hosts.Count == 0)
        {
            hosts[LocalHostName] = new HostSpec("localhost", Environment.UserName, null, null, ToYamlPath(workspaceRoot), CurrentOs());
        }

        var profiles = new Dictionary<string, ProfileSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in profilesNode.Children)
        {
            var name = RequireScalarKey(child.Key, "profile name");
            var profileNode = AsMapping(child.Value, $"profile '{name}'");

            RequireOnlyKeys(profileNode, ["description", "source", "build", "target", "publish-profile", "publish", "launch-profile", "env", "deploy", "run", "debug"], $"profiles.{name}");

            var publishProfile = GetOptionalString(profileNode, "publish-profile");
            var publishNode = GetOptionalMapping(profileNode, "publish");
            if (!string.IsNullOrWhiteSpace(publishProfile) && publishNode is not null)
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{name}' sets both 'publish-profile' and 'publish'; set at most one");
            }

            // The three host roles. `source` is the machine roam runs on; when the roamfile
            // defines exactly one host it can only be that one. `build` and `target` follow
            // `source` unless they say otherwise.
            var source = GetOptionalString(profileNode, "source") ?? DefaultSourceHost(name, hosts);
            var build = GetOptionalString(profileNode, "build") ?? source;
            var target = GetOptionalString(profileNode, "target") ?? source;
            var targetIsLocal = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);

            var env = GetOptionalStringMap(profileNode, "env");
            var deployNode = GetOptionalMapping(profileNode, "deploy") ?? EmptyMapping();
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

            // `publish:` is the default shape. An explicit `publish-profile:` still wins outright;
            // otherwise a missing block (or a block without `rid`) is filled from the target host's
            // declared OS and the controller's architecture.
            PublishSpec? publish = null;
            if (string.IsNullOrWhiteSpace(publishProfile))
            {
                if (publishNode is not null)
                {
                    RequireOnlyKeys(publishNode, ["rid", "self-contained", "configuration", "framework"], $"profiles.{name}.publish");
                }

                var targetOs = hosts.TryGetValue(target, out var targetSpec) ? targetSpec.Os : null;
                publish = new PublishSpec(
                    GetOptionalString(publishNode, "rid") ?? DefaultRid(name, targetOs),
                    GetOptionalBool(publishNode, "self-contained") ?? true,
                    // Only the fully synthesized block picks a configuration. An explicit `publish:`
                    // that omits it keeps dotnet's own default, so no existing profile changes shape.
                    GetOptionalString(publishNode, "configuration") ?? (publishNode is null ? "Release" : null),
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
                GetOptionalString(deployNode, "path") ?? DefaultDeployPath(workspaceRoot, projectName, targetIsLocal),
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
                source,
                build,
                target,
                publishProfile,
                publish,
                GetOptionalString(profileNode, "launch-profile"),
                env,
                deploy,
                run,
                debug);
        }

        if (profiles.Count == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml must define at least one profile");
        }

        ApplyWorkspaceDefaults(hosts, profiles, workspaceRoot, projectName);
        return new Roamfile(version, project, solution, csproj, hosts, profiles);
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

    // ---- Defaults ------------------------------------------------------------------------
    // Every helper below only fires when the corresponding key is absent, so an existing
    // roamfile parses to the same records it did before. See docs/configuration.md ("Defaults").

    // The single csproj under the roamfile directory, as a workspace-relative path. Mirrors the
    // discovery `roam init` performs, and the bin/obj filtering ProjectMetadataResolver uses when
    // it walks a solution.
    private static string DiscoverCsproj(string workspaceRoot)
    {
        var separator = Path.DirectorySeparatorChar;
        var candidates = Directory
            .GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"roamfile.yaml sets neither 'csproj' nor 'solution', and no .csproj was found under '{workspaceRoot}'");
        }

        if (candidates.Count > 1)
        {
            var listed = candidates.Take(5).Select(path => ToYamlPath(Path.GetRelativePath(workspaceRoot, path)));
            var suffix = candidates.Count > 5 ? $", and {candidates.Count - 5} more" : string.Empty;
            throw new RoamException(ExitCode.Config, "parse", "local", $"roamfile.yaml sets neither 'csproj' nor 'solution', and '{workspaceRoot}' contains {candidates.Count} csproj files ({string.Join(", ", listed)}{suffix}); set 'csproj' explicitly");
        }

        return ToYamlPath(Path.GetRelativePath(workspaceRoot, candidates[0]));
    }

    // Best-effort project name for path defaults only. Follows the same precedence
    // ProjectMetadataResolver.ResolveProjectPaths uses, and degrades to the workspace directory
    // name rather than throwing — the authoritative resolution still happens there.
    private static string ResolveProjectNameForDefaults(string workspaceRoot, string? csproj, string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            return project!;
        }

        if (!string.IsNullOrWhiteSpace(csproj))
        {
            return Path.GetFileNameWithoutExtension(csproj!);
        }

        return new DirectoryInfo(workspaceRoot).Name;
    }

    // `source` names the machine roam runs on. With exactly one host defined there is only one
    // answer; otherwise the profile has to say which.
    private static string DefaultSourceHost(string profileName, IReadOnlyDictionary<string, HostSpec> hosts)
    {
        if (hosts.Count == 1)
        {
            return hosts.Keys.First();
        }

        if (hosts.ContainsKey(LocalHostName))
        {
            return LocalHostName;
        }

        var known = string.Join(", ", hosts.Keys.OrderBy(x => x, StringComparer.Ordinal));
        throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' does not set 'source' and roamfile.yaml defines {hosts.Count} hosts ({known}); set 'source' explicitly or name one host '{LocalHostName}'");
    }

    // A roam-owned deploy directory. Local targets land beside the workspace (what `roam init`
    // scaffolds); remote targets land under the deploying user's home, which SyncEngine expands.
    private static string DefaultDeployPath(string workspaceRoot, string projectName, bool targetIsLocal)
        => targetIsLocal
            ? $"{ToYamlPath(workspaceRoot).TrimEnd('/')}/.roam-dev"
            : $"~/.roam/apps/{projectName}";

    // A build host needs a workspace to sync source into; SyncSourceAsync dereferences it
    // unconditionally. The source host is the controller, so its workspace is the workspace root;
    // every other host gets a roam-owned directory under the remote user's home.
    private static void ApplyWorkspaceDefaults(
        IDictionary<string, HostSpec> hosts,
        IReadOnlyDictionary<string, ProfileSpec> profiles,
        string workspaceRoot,
        string projectName)
    {
        var sourceHosts = new HashSet<string>(profiles.Values.Select(profile => profile.Source), StringComparer.OrdinalIgnoreCase);

        foreach (var name in hosts.Keys.ToList())
        {
            if (!string.IsNullOrWhiteSpace(hosts[name].Workspace))
            {
                continue;
            }

            hosts[name] = hosts[name] with
            {
                Workspace = sourceHosts.Contains(name)
                    ? ToYamlPath(workspaceRoot)
                    : $"~/.roam/src/{projectName}",
            };
        }
    }

    // Publish for the target host's declared OS on the controller's architecture. When the target
    // does not declare an OS, assume it matches the controller — the same assumption `roam init`
    // makes when it scaffolds a single-machine profile.
    private static string DefaultRid(string profileName, string? targetOs)
    {
        var os = targetOs switch
        {
            "windows" => "win",
            "macos" => "osx",
            "linux" => "linux",
            _ => CurrentOs() switch { "windows" => "win", "macos" => "osx", _ => "linux" },
        };

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' omits 'publish.rid' and roam cannot infer one for architecture '{RuntimeInformation.OSArchitecture}'; set 'publish.rid' explicitly"),
        };

        return $"{os}-{architecture}";
    }

    private static string CurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }

    // Roamfile paths are forward-slashed everywhere, including on a Windows controller, because
    // they are consumed by both the local filesystem and remote shells.
    private static string ToYamlPath(string path) => path.Replace('\\', '/');

    // ---- Parsing -------------------------------------------------------------------------

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

        return AsMapping(value, $"'{key}'");
    }

    // A key written with no body (`deploy:`, `local:`) parses as an empty scalar. Treat it as an
    // empty mapping so every block whose fields all have defaults can be left blank.
    private static YamlMappingNode AsMapping(YamlNode node, string description)
    {
        if (node is YamlMappingNode mapping)
        {
            return mapping;
        }

        if (node is YamlScalarNode scalar && string.IsNullOrWhiteSpace(scalar.Value))
        {
            return EmptyMapping();
        }

        throw new RoamException(ExitCode.Config, "parse", "local", $"{description} must be a mapping");
    }

    private static YamlMappingNode EmptyMapping() => new();

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
