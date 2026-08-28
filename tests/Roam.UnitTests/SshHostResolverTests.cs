using Xunit;

namespace Roam.UnitTests;

public sealed class SshHostResolverTests
{
    [Fact]
    public void BuildSshCommandWrapsWindowsTargetsInPowerShell()
    {
        var resolver = new SshHostResolver();
        var host = new HostResolution("target", "winhost", "tester", 22, null, [], null, null, "windows", false);

        var command = resolver.BuildSshCommand(host, "Get-Process");

        Assert.Contains("powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand", command);
        Assert.DoesNotContain("Get-Process", command);
    }

    [Fact]
    public void BuildSshCommandLeavesUnixTargetsUntouched()
    {
        var resolver = new SshHostResolver();
        var host = new HostResolution("target", "kiosk", "tester", 22, null, [], null, null, "linux", false);

        var command = resolver.BuildSshCommand(host, "true");

        Assert.DoesNotContain("powershell.exe", command);
        Assert.Contains("'true'", command);
    }

    [Fact]
    public void BuildScpToRemoteCommandAddsRecursiveFlagWhenRequested()
    {
        var resolver = new SshHostResolver();
        var host = new HostResolution("target", "winhost", "tester", 22, null, [], null, null, "windows", false);

        var command = resolver.BuildScpToRemoteCommand(host, "/tmp/publish/.", "/C:/roam/demo/", recursive: true);

        Assert.Contains("scp -q -P 22 -r", command);
        Assert.Contains("'/tmp/publish/.'", command);
        Assert.Contains("'tester@winhost:/C:/roam/demo/'", command);
    }

    // Regression guard for the Windows-controller transport bug: the remote payload must be the
    // single, final, VERBATIM argv entry (no added quoting). That is exactly what lets an
    // env-prefixed / nested-quoted command survive argv -> ssh -> remote shell without a local
    // shell (bash or pwsh) re-parsing it. A Unix target passes the command through untouched.
    [Fact]
    public void BuildSshArgsPassesUnixRemotePayloadAsSingleVerbatimArg()
    {
        var resolver = new SshHostResolver();
        var host = new HostResolution("target", "kiosk", "tester", 22, null, [], null, null, "linux", false);
        var payload = "nohup sh -c 'DOTNET_DbgEnableMiniDump='\"'\"'1'\"'\"' /opt/app/App' < /dev/null > '/opt/app/x.out' 2>&1 &";

        var args = resolver.BuildSshArgs(host, payload);

        Assert.Equal(payload, args[^1]);          // remote command: last arg, byte-for-byte unchanged
        Assert.Equal("tester@kiosk", args[^2]);   // preceded by user@host
    }

    // A Windows target's payload is wrapped (base64 powershell.exe), but still a single argv entry.
    [Fact]
    public void BuildSshArgsWrapsWindowsTargetPayloadAsSingleArg()
    {
        var resolver = new SshHostResolver();
        var host = new HostResolution("target", "winbox", "tester", 22, null, [], null, null, "windows", false);

        var args = resolver.BuildSshArgs(host, "Get-Process");

        Assert.Contains("powershell.exe", args[^1]);
        Assert.Contains("-EncodedCommand", args[^1]);
        Assert.DoesNotContain("Get-Process", args[^1]);   // base64-encoded, not literal
    }
}
