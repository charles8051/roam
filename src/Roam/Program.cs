using System.Text;
using Roam;

return await CliProgram.RunAsync(args);

internal static class CliProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Force UTF-8 output so non-ASCII glyphs in help text and per-step lines
        // (em-dash, ✓, ✗) render correctly on Windows consoles whose default
        // code page (cp437/cp1252) cannot represent them. Safe on Linux/macOS
        // where the terminal is already UTF-8.
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* console may not be available */ }
        try
        {
            var command = Parse(args, out var cli);
            RoamLog.Configure(cli);
            RoamLog.Event("cli.start", "roam command starting", new Dictionary<string, object?>
            {
                ["command"] = command.Name,
                ["args"] = args,
                ["cwd"] = Directory.GetCurrentDirectory(),
                ["version"] = VersionInfo.Current,
            });
            var runner = new RoamCommands();
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var outcome = command.Name switch
            {
                "init" => await runner.RunInitAsync(cli, command.GetOption("solution"), command.GetOption("csproj"), command.HasFlag("force"), cancellation.Token),
                "run" => await runner.RunPipelineAsync(cli, command.RequireArgument(0), command.GetOption("source"), command.GetOption("build"), command.GetOption("target"), cancellation.Token),
                "deploy" => await runner.RunDeployAsync(cli, command.RequireArgument(0), command.GetOption("source"), command.GetOption("build"), command.GetOption("target"), cancellation.Token),
                "attach" => await runner.RunAttachAsync(cli, command.RequireArgument(0), command.GetOption("output"), command.HasFlag("regenerate"), cancellation.Token),
                "diag" => await runner.RunDiagAsync(cli, command.RequireArgument(0), command.GetOption("out"), command.HasFlag("logs"), command.HasFlag("dump"), command.GetOption("trace"), command.GetOption("since"), command.HasFlag("json"), command.HasFlag("keep-remote"), cancellation.Token),
                "uninstall" => await runner.RunUninstallAsync(cli, command.RequireArgument(0), command.HasFlag("keep-manifest"), command.HasFlag("dry-run"), cancellation.Token),
                _ => throw new RoamException(ExitCode.Usage, "parse", "local", $"unknown command '{command.Name}'"),
            };

            RoamLog.Event("cli.end", "roam command completed", new Dictionary<string, object?>
            {
                ["exitCode"] = (int)outcome.ExitCode,
            });

            return (int)outcome.ExitCode;
        }
        catch (OperationCanceledException)
        {
            RoamLog.Event("cli.cancelled", "roam command cancelled");
            return 130;
        }
        catch (RoamException ex)
        {
            RoamLog.Event("cli.error", "roam command failed", new Dictionary<string, object?>
            {
                ["exitCode"] = (int)ex.ExitCode,
                ["step"] = ex.Step,
                ["host"] = ex.Host,
                ["message"] = ex.Message,
            });
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine($"roam: exit={(int)ex.ExitCode} step={ex.Step} host={ex.Host}");
            return (int)ex.ExitCode;
        }
        catch (Exception ex)
        {
            RoamLog.Event("cli.error", "roam command failed internally", new Dictionary<string, object?>
            {
                ["type"] = ex.GetType().FullName,
                ["message"] = ex.Message,
                ["stack"] = ex.StackTrace,
            });
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("roam: exit=10 step=internal host=local");
            return 10;
        }
    }

    private static ParsedCommand Parse(string[] args, out CliOptions cli)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintTopLevelHelp();
            Environment.Exit(0);
        }

        if (args[0] == "--version")
        {
            Console.WriteLine(VersionInfo.Current);
            Environment.Exit(0);
        }

        string? roamfile = null;
        string? logFile = null;
        var verbose = false;
        var quiet = false;
        var noColor = false;
        var index = 0;

        while (index < args.Length)
        {
            switch (args[index])
            {
                case "-f":
                case "--roamfile":
                    roamfile = RequireValue(args, ref index, args[index]);
                    index++;
                    continue;
                case "-v":
                case "--verbose":
                    verbose = true;
                    index++;
                    continue;
                case "-q":
                case "--quiet":
                    quiet = true;
                    index++;
                    continue;
                case "--log-file":
                    logFile = RequireValue(args, ref index, args[index]);
                    index++;
                    continue;
                case "--no-color":
                    noColor = true;
                    index++;
                    continue;
            }

            break;
        }

        if (verbose && quiet)
        {
            throw new RoamException(ExitCode.Usage, "parse", "local", "-v/--verbose and -q/--quiet are mutually exclusive");
        }

        cli = new CliOptions(roamfile, verbose, quiet, logFile, noColor);

        if (index >= args.Length)
        {
            PrintTopLevelHelp();
            Environment.Exit(0);
        }

        var commandName = args[index++];
        var remaining = args.Skip(index).ToArray();

        if (remaining.Contains("--help") || remaining.Contains("-h"))
        {
            PrintCommandHelp(commandName);
            Environment.Exit(0);
        }

        return ParsedCommand.Parse(commandName, remaining);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new RoamException(ExitCode.Usage, "parse", "local", $"missing value for {option}");
        }

        index++;
        return args[index];
    }

    private static void PrintTopLevelHelp()
    {
        Console.WriteLine("roam — build .NET on any host, run on any host, debug from anywhere.\n");
        Console.WriteLine("Usage: roam <command> [options]\n");
        Console.WriteLine("Commands:");
        Console.WriteLine("  init                 Scaffold roamfile.yaml in the current directory.");
        Console.WriteLine("  run <profile>        Run the pipeline for a profile.");
        Console.WriteLine("  deploy <profile>     Sync bytes for a profile (register, don't start); runs no app.");
        Console.WriteLine("  attach <profile>     Emit VSCode attach config for a profile.");
        Console.WriteLine("  diag <profile>       Fetch a diagnostics bundle (logs, crash dumps) from the target.");
        Console.WriteLine("  uninstall <profile>  Tear down a deployed profile and wipe its manifest.\n");
        Console.WriteLine("Global options:");
        Console.WriteLine("  -f, --roamfile <path>   Path to roamfile.yaml (default: walk up from cwd).");
        Console.WriteLine("  -v, --verbose           Enable debug logging.");
        Console.WriteLine("  -q, --quiet             Suppress per-step output; errors only.");
        Console.WriteLine("      --log-file <path>   Write JSONL log to <path>.");
        Console.WriteLine("      --no-color          Disable ANSI color.");
        Console.WriteLine("  -h, --help              Show help.");
        Console.WriteLine("      --version           Show version.\n");
        Console.WriteLine("See https://github.com/charles8051/roam for documentation.");
    }

    private static void PrintCommandHelp(string command)
    {
        switch (command)
        {
            case "run":
                Console.WriteLine("roam run — execute the fixed pipeline for a profile.\n");
                Console.WriteLine("Usage: roam run <profile> [options]\n");
                Console.WriteLine("Arguments:");
                Console.WriteLine("  <profile>   Profile name as declared in roamfile.yaml.\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --source <host>   Override the source host for this invocation.");
                Console.WriteLine("      --build <host>    Override the build host for this invocation.");
                Console.WriteLine("      --target <host>   Override the target host for this invocation.\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            case "deploy":
                Console.WriteLine("roam deploy — sync a profile's bytes to the target without starting it.\n");
                Console.WriteLine("Usage: roam deploy <profile> [options]\n");
                Console.WriteLine("Runs sync-source → publish → stop → sync-artifacts, then stops. No start, run,");
                Console.WriteLine("or ready. For an interactive-session profile it registers the Roam_<profile>");
                Console.WriteLine("scheduled task but does NOT start it, so an external launcher owns start");
                Console.WriteLine("(schtasks /Run). For a non-interactive profile it is pure byte delivery and");
                Console.WriteLine("launches nothing.\n");
                Console.WriteLine("Arguments:");
                Console.WriteLine("  <profile>   Profile name as declared in roamfile.yaml.\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --source <host>   Override the source host for this invocation.");
                Console.WriteLine("      --build <host>    Override the build host for this invocation.");
                Console.WriteLine("      --target <host>   Override the target host for this invocation.\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            case "attach":
                Console.WriteLine("roam attach — emit a VSCode launch.json entry for a profile.\n");
                Console.WriteLine("Usage: roam attach <profile> [options]\n");
                Console.WriteLine("Arguments:");
                Console.WriteLine("  <profile>   Profile name as declared in roamfile.yaml.\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --output <path>   launch.json path (default: .vscode/launch.json).");
                Console.WriteLine("      --regenerate      Rewrite the entry even if it already exists.\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            case "diag":
                Console.WriteLine("roam diag — fetch a read-only diagnostics bundle from a profile's target.\n");
                Console.WriteLine("Usage: roam diag <profile> [options]\n");
                Console.WriteLine("Captures into .roam/diag/<profile>/<run-id>/ with a machine-readable diag.json index.");
                Console.WriteLine("Default tier is logs (the roam-redirected process output + deploy.diag.logs files +");
                Console.WriteLine("journald when a unit is named). Read-only on the target.\n");
                Console.WriteLine("Arguments:");
                Console.WriteLine("  <profile>   Profile name as declared in roamfile.yaml.\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --out <dir>       Bundle output dir (default: .roam/diag/<profile>/<run-id>).");
                Console.WriteLine("      --logs            Capture the logs tier (default when no tier flag is given).");
                Console.WriteLine("      --dump            Also fetch crash dumps from <deploy.path>/.roam-diag/dumps/.");
                Console.WriteLine("      --trace <secs>    Live trace tier (not yet implemented in this build).");
                Console.WriteLine("      --since <when>    journald window for the unit capture (e.g. '1 hour ago').");
                Console.WriteLine("      --json            Print the diag.json index to stdout.");
                Console.WriteLine("      --keep-remote     Leave any target-side scratch in place (trace/bundled tier).\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            case "init":
                Console.WriteLine("roam init — scaffold roamfile.yaml in the current directory.\n");
                Console.WriteLine("Usage: roam init [options]\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --solution <path>   Explicit .sln path.");
                Console.WriteLine("      --csproj <path>     Explicit .csproj path.");
                Console.WriteLine("      --force             Overwrite an existing roamfile.yaml.\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            case "uninstall":
                Console.WriteLine("roam uninstall — tear down a deployed profile and wipe its manifest.\n");
                Console.WriteLine("Usage: roam uninstall <profile> [options]\n");
                Console.WriteLine("Arguments:");
                Console.WriteLine("  <profile>   Profile name as declared in roamfile.yaml.\n");
                Console.WriteLine("Options:");
                Console.WriteLine("      --keep-manifest   Preserve .roam/manifests/<profile>/ (next run stays warm).");
                Console.WriteLine("      --dry-run         Print the uninstall commands without running them.\n");
                Console.WriteLine("  ... plus global options (see roam --help).");
                break;
            default:
                throw new RoamException(ExitCode.Usage, "parse", "local", $"unknown command '{command}'");
        }
    }

    private sealed class ParsedCommand
    {
        private ParsedCommand(string name, List<string> arguments, Dictionary<string, string?> options, HashSet<string> flags)
        {
            Name = name;
            Arguments = arguments;
            Options = options;
            Flags = flags;
        }

        public string Name { get; }

        public List<string> Arguments { get; }

        public Dictionary<string, string?> Options { get; }

        public HashSet<string> Flags { get; }

        public static ParsedCommand Parse(string name, string[] args)
        {
            var arguments = new List<string>();
            var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                switch (arg)
                {
                    case "--force":
                    case "--regenerate":
                    case "--keep-manifest":
                    case "--dry-run":
                    case "--logs":
                    case "--dump":
                    case "--json":
                    case "--keep-remote":
                        flags.Add(arg.TrimStart('-'));
                        break;
                    case "--solution":
                    case "--csproj":
                    case "--source":
                    case "--build":
                    case "--target":
                    case "--output":
                    case "--out":
                    case "--trace":
                    case "--since":
                        if (index + 1 >= args.Length)
                        {
                            throw new RoamException(ExitCode.Usage, "parse", "local", $"missing value for {arg}");
                        }
                        options[arg.TrimStart('-')] = args[++index];
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                        {
                            throw new RoamException(ExitCode.Usage, "parse", "local", $"unknown flag '{arg}'");
                        }
                        arguments.Add(arg);
                        break;
                }
            }

            if ((name == "run" || name == "deploy" || name == "attach" || name == "diag" || name == "uninstall") && arguments.Count != 1)
            {
                throw new RoamException(ExitCode.Usage, "parse", "local", $"roam {name} requires exactly one <profile> argument");
            }

            return new ParsedCommand(name, arguments, options, flags);
        }

        public string RequireArgument(int index)
        {
            if (Arguments.Count <= index)
            {
                throw new RoamException(ExitCode.Usage, "parse", "local", "missing required positional argument");
            }

            return Arguments[index];
        }

        public string? GetOption(string name)
            => Options.TryGetValue(name, out var value) ? value : null;

        public bool HasFlag(string name)
            => Flags.Contains(name);
    }
}
