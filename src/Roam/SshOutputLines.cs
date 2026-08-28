namespace Roam;

// Functional core (no IO, no clock, no state): selecting the line of a captured stdout/stderr
// blob that a human should see. The single source of truth for "which line is the real error",
// shared by every step that surfaces an ssh/process failure to the user (start, stop, sync,
// publish, preflight, ready, one-shot, uninstall).
//
// The motivating bug (charles8051/roam#7): over SSH, stderr almost always *opens* with one or
// more benign warnings -- a missing identity file, the no-pty notice, or OpenSSH's post-quantum
// advisory -- before any real error. Naively returning the first non-empty line surfaces the
// warning and masks the actual failure. So both selectors strip the known-benign ssh noise first,
// and only then pick a meaningful line.
internal static class SshOutputLines
{
    // Error-ish keywords that promote a line to "this is the real failure". Matched
    // case-insensitively as substrings; "Failed" is also matched as a line prefix. "denied"
    // catches the canonical Windows case (e.g. "Register-ScheduledTask : Access is denied").
    // Kept reasonably specific -- a bare "cannot" was dropped because it fires on benign prose
    // ("cannot be determined") far more often than it names a failure.
    private static readonly string[] ErrorMarkers =
    [
        "error", "fatal", "denied", "exception", "unauthorized",
        "refused", "not recognized",
    ];

    // First line worth showing: the first non-empty line that is not benign ssh noise. Falls back
    // to the first non-empty line if every line is benign (so we never hide all output), and to
    // null only when there was no output at all.
    internal static string? FirstMeaningful(string? text)
    {
        var lines = SplitLines(text);
        if (lines.Length == 0)
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (!IsBenignSshNoise(line))
            {
                return line;
            }
        }

        return lines[0];
    }

    // Best guess at the line that names the failure. Strips benign ssh noise, then prefers the
    // first line carrying an error marker; otherwise returns the last meaningful line (tools like
    // dotnet/msbuild open with banner output, so the *last* line is a better guess than the first).
    // Falls back to the raw lines if filtering left nothing, and to null only when there was no
    // output at all.
    internal static string? BestError(string? text)
    {
        var lines = SplitLines(text);
        if (lines.Length == 0)
        {
            return null;
        }

        var meaningful = lines.Where(line => !IsBenignSshNoise(line)).ToArray();
        if (meaningful.Length == 0)
        {
            meaningful = lines;
        }

        foreach (var line in meaningful)
        {
            if (line.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                || ErrorMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return line;
            }
        }

        return meaningful[^1];
    }

    // True for lines OpenSSH emits as routine diagnostics over a non-interactive channel. These are
    // never the reason a command failed; an exit != 0 always has a more specific cause deeper in
    // the stream. Pure predicate -- keep it conservative (only match well-known phrasings) so a
    // genuine error is never mistaken for noise.
    internal static bool IsBenignSshNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();

        // Each match is anchored on two co-occurring tokens (not one loose substring) so a real
        // error line that merely mentions one of these words -- "Identity file successfully
        // loaded", "Permanently added to allowlist" -- is not mistaken for noise.

        // "Warning: Identity file /c/Users/.../id_rsa not accessible: No such file or directory."
        if (Has(trimmed, "Identity file") && Has(trimmed, "not accessible"))
        {
            return true;
        }

        // "Pseudo-terminal will not be allocated because stdin is not a terminal."
        if (Has(trimmed, "Pseudo-terminal will not be allocated"))
        {
            return true;
        }

        // "Warning: Permanently added '<host>' (<type>) to the list of known hosts."
        if (Has(trimmed, "Permanently added") && Has(trimmed, "known hosts"))
        {
            return true;
        }

        // OpenSSH post-quantum advisory block. The three lines are each prefixed with "**", but a
        // bare "**" prefix also matches markdown/build-banner output, so we key off the advisory's
        // own phrasings -- which already cover every line of the block -- instead.
        // ("** WARNING: connection is not using a post-quantum key exchange algorithm.",
        //  "** This session may be vulnerable to a store-now-decrypt-later quantum attack.",
        //  "** See https://openssh.com/pq.html for more information.")
        return Has(trimmed, "post-quantum key exchange")
            || Has(trimmed, "This session may be vulnerable")
            || Has(trimmed, "store-now-decrypt-later")
            || Has(trimmed, "openssh.com/pq.html");
    }

    private static bool Has(string line, string needle)
        => line.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string[] SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
