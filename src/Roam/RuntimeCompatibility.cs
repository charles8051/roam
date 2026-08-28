using System.Globalization;

namespace Roam;

// Pure helpers behind the framework-dependent-publish preflight. Kept free of any I/O so the
// parsing and the compatibility decision are unit-testable without a target host.
public static class RuntimeCompatibility
{
    // "net10.0" -> 10.0, "net10.0-windows" -> 10.0, "net9.0" -> 9.0. The OS suffix (-windows,
    // -android, ...) is irrelevant to which shared runtime the target needs. Returns null for
    // anything we don't recognise (older netcoreappX.Y, netstandard, garbage) so the caller can
    // warn-and-proceed rather than block on a framework moniker it can't reason about.
    public static Version? ParseTargetFrameworkVersion(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        var moniker = targetFramework.Trim().Split('-', 2)[0];
        if (!moniker.StartsWith("net", StringComparison.Ordinal))
        {
            return null;
        }

        var versionText = moniker[3..];
        var parts = versionText.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        return new Version(major, minor);
    }

    // Pulls the Microsoft.NETCore.App versions out of `dotnet --list-runtimes` stdout. Lines look
    // like "Microsoft.NETCore.App 10.0.3 [/path]"; ASP.NET Core and WindowsDesktop lines are
    // ignored — every framework-dependent app needs the base runtime, and checking the base
    // runtime keeps the false-positive rate low for the Avalonia apps this workspace deploys.
    public static IReadOnlyList<Version> ParseInstalledRuntimes(string listRuntimesOutput)
    {
        var versions = new List<Version>();
        foreach (var rawLine in listRuntimesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !string.Equals(tokens[0], "Microsoft.NETCore.App", StringComparison.Ordinal))
            {
                continue;
            }

            // Runtime versions can carry a pre-release suffix (10.0.0-rc.1.25...); System.Version
            // rejects it, so cut at the first '-' before parsing.
            var numeric = tokens[1].Split('-', 2)[0];
            if (Version.TryParse(numeric, out var version))
            {
                versions.Add(version);
            }
        }

        return versions;
    }

    // Mirrors the default .NET host roll-forward policy (Minor): an app targeting major.minor runs
    // on any installed runtime of the SAME major with an equal-or-higher minor. The host does not
    // roll forward across majors by default, so a 10.0 app is not satisfied by an 11.0 runtime.
    public static bool IsCompatible(Version required, IReadOnlyList<Version> installed)
    {
        foreach (var version in installed)
        {
            if (version.Major == required.Major && version.Minor >= required.Minor)
            {
                return true;
            }
        }

        return false;
    }

    // The OS family a .NET RID publishes for: "windows" | "linux" | "macos", or null when the RID's
    // OS portion isn't one roam deploys to (e.g. freebsd, or a custom RID). Prefix-matched so it
    // covers both the portable RIDs (win-x64, linux-x64, osx-arm64) and the legacy specific ones
    // (win10-x64, linux-musl-arm64, osx.13-arm64).
    public static string? RidOperatingSystem(string? runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            return null;
        }

        var rid = runtimeIdentifier.Trim().ToLowerInvariant();
        if (rid.StartsWith("win", StringComparison.Ordinal))
        {
            return "windows";
        }

        if (rid.StartsWith("linux", StringComparison.Ordinal))
        {
            return "linux";
        }

        if (rid.StartsWith("osx", StringComparison.Ordinal))
        {
            return "macos";
        }

        return null;
    }

    // Catches the silent footgun where a profile's publish RID names a different OS than the target
    // host: a leftover `win-x64` shipped to an `os: linux` target publishes a Windows apphost that
    // only fails at `start`, after a full publish + sync. Returns an actionable error message on a
    // confident mismatch, or null when there's nothing to flag (no RID, unknown RID OS family, or
    // unknown target OS) — fail-open on what we can't reason about, hard-fail on a known mismatch.
    public static string? ValidatePublishOsTargetsHost(string? runtimeIdentifier, string? targetOs)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier) || string.IsNullOrWhiteSpace(targetOs))
        {
            return null;
        }

        var ridOs = RidOperatingSystem(runtimeIdentifier);
        if (ridOs is null || string.Equals(ridOs, targetOs, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"publish RID '{runtimeIdentifier}' targets {ridOs}, but the target host is os={targetOs}. "
            + $"Set publish.rid (or the pubxml RuntimeIdentifier) to a {targetOs} RID (e.g. {SuggestRidForOs(targetOs)}).";
    }

    private static string SuggestRidForOs(string os)
        => os.Trim().ToLowerInvariant() switch
        {
            "windows" => "win-x64",
            "linux" => "linux-x64",
            "macos" => "osx-arm64",
            _ => $"{os}-x64",
        };
}
