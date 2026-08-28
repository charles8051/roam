using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

var options = SampleOptions.Parse(args);

if (options.MarkerDirectory is not null)
{
    Directory.CreateDirectory(options.MarkerDirectory);
    await File.WriteAllTextAsync(
        Path.Combine(options.MarkerDirectory, "startup.txt"),
        $"mode={options.Mode}{Environment.NewLine}");
}

if (options.Mode == SampleMode.CrashOnStart)
{
    Console.Error.WriteLine("SampleApp crash-on-start requested.");
    return 42;
}

if (options.Mode == SampleMode.DelayedStart && options.DelaySeconds > 0)
{
    await Task.Delay(TimeSpan.FromSeconds(options.DelaySeconds));
}

if (options.ManifestPath is not null)
{
    var manifestDirectory = Path.GetDirectoryName(options.ManifestPath);

    if (!string.IsNullOrWhiteSpace(manifestDirectory))
    {
        Directory.CreateDirectory(manifestDirectory);
    }

    var manifest = new
    {
        pid = Environment.ProcessId,
        mode = options.Mode.ToString(),
        startedUtc = DateTimeOffset.UtcNow,
        machine = Environment.MachineName
    };

    await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(manifest));
}

if (options.ReadyFile is not null)
{
    var readyDirectory = Path.GetDirectoryName(options.ReadyFile);

    if (!string.IsNullOrWhiteSpace(readyDirectory))
    {
        Directory.CreateDirectory(readyDirectory);
    }

    await File.WriteAllTextAsync(options.ReadyFile, "ready");
}

Console.WriteLine($"SampleApp ready in mode '{options.Mode}'. PID={Environment.ProcessId}");

if (options.ExitAfterSeconds > 0)
{
    await Task.Delay(TimeSpan.FromSeconds(options.ExitAfterSeconds));
    return 0;
}

var cancellation = new CancellationTokenSource();

PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cancellation.Cancel());
PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => cancellation.Cancel());

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
}
catch (OperationCanceledException)
{
}

if (options.MarkerDirectory is not null)
{
    await File.WriteAllTextAsync(
        Path.Combine(options.MarkerDirectory, "shutdown.txt"),
        DateTimeOffset.UtcNow.ToString("O"));
}

return 0;

internal enum SampleMode
{
    Healthy,
    CrashOnStart,
    DelayedStart
}

internal sealed record SampleOptions(
    SampleMode Mode,
    int DelaySeconds,
    int ExitAfterSeconds,
    string? ReadyFile,
    string? ManifestPath,
    string? MarkerDirectory)
{
    public static SampleOptions Parse(string[] args)
    {
        var argMap = ParseArgs(args);

        var modeText = GetValue(argMap, "mode")
            ?? Environment.GetEnvironmentVariable("SAMPLEAPP_MODE")
            ?? "healthy";

        var mode = modeText.ToLowerInvariant() switch
        {
            "healthy" => SampleMode.Healthy,
            "crash-on-start" => SampleMode.CrashOnStart,
            "delayed-start" => SampleMode.DelayedStart,
            _ => throw new InvalidOperationException($"Unsupported SAMPLEAPP_MODE '{modeText}'.")
        };

        return new SampleOptions(
            mode,
            ParseInt(GetValue(argMap, "delay-seconds") ?? Environment.GetEnvironmentVariable("SAMPLEAPP_DELAY_SECONDS")),
            ParseInt(GetValue(argMap, "exit-after-seconds") ?? Environment.GetEnvironmentVariable("SAMPLEAPP_EXIT_AFTER_SECONDS")),
            GetValue(argMap, "ready-file") ?? Environment.GetEnvironmentVariable("SAMPLEAPP_READY_FILE"),
            GetValue(argMap, "manifest-path") ?? Environment.GetEnvironmentVariable("SAMPLEAPP_MANIFEST_PATH"),
            GetValue(argMap, "marker-dir") ?? Environment.GetEnvironmentVariable("SAMPLEAPP_MARKER_DIR"));
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = argument[2..];
            string? value = null;

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[index + 1];
                index++;
            }

            values[key] = value;
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static int ParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : 0;
}