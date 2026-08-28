using System.Text.Json;

namespace Roam;

public static class RoamLog
{
    private static readonly object Gate = new();
    private static bool _verbose;
    private static string? _logFile;

    public static void Configure(CliOptions options)
    {
        _verbose = options.Verbose;
        _logFile = string.IsNullOrWhiteSpace(options.LogFile) ? null : Path.GetFullPath(options.LogFile);

        if (_logFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            File.AppendAllText(_logFile, string.Empty);
        }
    }

    public static void Event(string name, string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        var line = JsonSerializer.Serialize(new LogEvent(DateTimeOffset.UtcNow, name, message, data));
        lock (Gate)
        {
            if (_logFile is not null)
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
        }

        if (_verbose)
        {
            Console.Error.WriteLine($"  debug {name}: {message}");
        }
    }

    public static IDisposable Scope(string name, string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        Event(name + ".start", message, data);
        return new LogScope(name, message);
    }

    private sealed class LogScope(string name, string message) : IDisposable
    {
        private readonly long _started = Environment.TickCount64;

        public void Dispose()
        {
            Event(name + ".end", message, new Dictionary<string, object?>
            {
                ["elapsedMs"] = Environment.TickCount64 - _started,
            });
        }
    }

    private sealed record LogEvent(
        DateTimeOffset Timestamp,
        string Event,
        string Message,
        IReadOnlyDictionary<string, object?>? Data);
}
