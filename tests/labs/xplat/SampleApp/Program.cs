// Minimal cross-platform deploy + diag smoke target for roam's E2E lab. It prints a marker (so the
// deployed binary can be byte-checked), AND writes the marker to a log file next to the binary so
// `roam diag` (deploy.diag.logs) has an operator log to fetch on any target OS — the universal
// artifact, since a Windows interactive-session task has no roam-redirected stdout. Then it stays
// alive so roam's `ready` step (pgrep / Get-Process) sees a running process; roam stops it via the
// profile's `stop:` hook.
var marker = "roam-xplat sample CROSSPLAT_MARKER_V1";
Console.WriteLine(marker);
Console.Out.Flush();

// AppContext.BaseDirectory is the deploy dir for a self-contained, flatten-published app, so this
// resolves to <deploy.path>/sampleapp.diag.log on every target.
var logPath = Path.Combine(AppContext.BaseDirectory, "sampleapp.diag.log");
try
{
    File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {marker} pid={Environment.ProcessId}{Environment.NewLine}");
}
catch
{
    // Best effort: the stdout marker (and the roam-redirected .out on Unix) remain.
}

while (true)
{
    Thread.Sleep(TimeSpan.FromMinutes(1));
}
