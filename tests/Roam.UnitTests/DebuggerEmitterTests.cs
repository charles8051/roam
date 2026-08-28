using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Roam.UnitTests;

public sealed class DebuggerEmitterTests
{
    private static HostResolution Host(
        string user = "dev",
        string sshHost = "kiosk.local",
        int port = 22,
        string? identityFile = null,
        string? proxyJump = null,
        string? os = "linux")
        => new(
            Name: "target",
            SshHost: sshHost,
            User: user,
            Port: port,
            IdentityFile: identityFile,
            IdentityFiles: identityFile is null ? [] : new[] { identityFile },
            ProxyJump: proxyJump,
            Workspace: null,
            Os: os,
            IsLocal: false);

    private static DebugSpec Debug(string processName = "KioskUi")
        => new(Enabled: true, Debugger: "vsdbg", Editor: "vscode", ProcessName: processName, InstallOnTarget: false);

    private static async Task<JsonObject> EmitAndReadAsync(
        HostResolution host,
        string localSourceRoot = "C:/repos/kiosk-ui",
        string localProjectDirectory = "C:/repos/kiosk-ui/src/KioskUi",
        string remoteProjectDirectory = "/home/deploy/src/kiosk-ui/src/KioskUi")
    {
        var temp = Directory.CreateTempSubdirectory("roam-attach-");
        try
        {
            var launchPath = Path.Combine(temp.FullName, ".vscode", "launch.json");
            await DebuggerEmitter.EmitAsync(
                launchPath,
                "kiosk",
                localSourceRoot,
                localProjectDirectory,
                remoteProjectDirectory,
                host,
                Debug(),
                CancellationToken.None);

            var content = await File.ReadAllTextAsync(launchPath);
            return (JsonNode.Parse(content) as JsonObject)!;
        }
        finally
        {
            temp.Delete(true);
        }
    }

    private static JsonObject GetRoamConfig(JsonObject root)
    {
        var configurations = (JsonArray)root["configurations"]!;
        foreach (var node in configurations)
        {
            if (node is JsonObject obj && obj["name"]?.GetValue<string>() == "roam: kiosk")
            {
                return obj;
            }
        }
        throw new InvalidOperationException("emitted entry not found");
    }

    [Fact]
    public async Task LinuxTargetUsesTildeRelativeDebuggerPath()
    {
        var root = await EmitAndReadAsync(Host(os: "linux"));
        var entry = GetRoamConfig(root);
        var pipe = (JsonObject)entry["pipeTransport"]!;

        Assert.Equal("~/.vsdbg/vsdbg", pipe["debuggerPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task WindowsTargetUsesExplicitUserPath()
    {
        var root = await EmitAndReadAsync(Host(user: "deploy", os: "windows"));
        var entry = GetRoamConfig(root);
        var pipe = (JsonObject)entry["pipeTransport"]!;

        // No tilde — OpenSSH's default shell on Windows is cmd.exe which doesn't expand it.
        // Must end with .exe so OpenSSH dispatches it as a Windows executable, not a missing posix file.
        Assert.Equal("C:/Users/deploy/.vsdbg/vsdbg.exe", pipe["debuggerPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task PipeArgsIncludeOnlyHostByDefault()
    {
        var root = await EmitAndReadAsync(Host(user: "deploy", sshHost: "buildhost", port: 22));
        var pipe = (JsonObject)GetRoamConfig(root)["pipeTransport"]!;
        var args = ((JsonArray)pipe["pipeArgs"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Equal(new[] { "-T", "deploy@buildhost" }, args);
    }

    [Fact]
    public async Task PipeArgsForwardNonDefaultPort()
    {
        var root = await EmitAndReadAsync(Host(port: 2222));
        var pipe = (JsonObject)GetRoamConfig(root)["pipeTransport"]!;
        var args = ((JsonArray)pipe["pipeArgs"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("-p", args);
        Assert.Contains("2222", args);
    }

    [Fact]
    public async Task PipeArgsForwardIdentityFileAndProxyJump()
    {
        var root = await EmitAndReadAsync(Host(
            identityFile: "C:/Users/dev/.ssh/kiosk_ed25519",
            proxyJump: "bastion@edge.example.com"));
        var pipe = (JsonObject)GetRoamConfig(root)["pipeTransport"]!;
        var args = ((JsonArray)pipe["pipeArgs"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("-i", args);
        Assert.Contains("C:/Users/dev/.ssh/kiosk_ed25519", args);
        Assert.Contains("-J", args);
        Assert.Contains("bastion@edge.example.com", args);
    }

    [Fact]
    public async Task PipeCwdAndSourceFileMapUseForwardSlashes()
    {
        var root = await EmitAndReadAsync(
            Host(os: "windows"),
            localSourceRoot: @"C:\repos\kiosk-ui",
            localProjectDirectory: @"C:\repos\kiosk-ui\src\KioskUi",
            remoteProjectDirectory: @"C:\Users\deploy\ExampleApp\Ui");

        var entry = GetRoamConfig(root);
        var pipe = (JsonObject)entry["pipeTransport"]!;
        var sourceMap = (JsonObject)entry["sourceFileMap"]!;

        // pipeCwd
        Assert.Equal("C:/repos/kiosk-ui", pipe["pipeCwd"]!.GetValue<string>());
        // sourceFileMap: both key (remote) and value (local) normalized
        Assert.True(sourceMap.ContainsKey("C:/Users/deploy/ExampleApp/Ui"));
        Assert.Equal("C:/repos/kiosk-ui/src/KioskUi", sourceMap["C:/Users/deploy/ExampleApp/Ui"]!.GetValue<string>());
        Assert.DoesNotContain(@"\", pipe["pipeCwd"]!.GetValue<string>());
    }

    [Fact]
    public async Task QuoteArgsIsEnabled()
    {
        var root = await EmitAndReadAsync(Host());
        var pipe = (JsonObject)GetRoamConfig(root)["pipeTransport"]!;

        Assert.True(pipe["quoteArgs"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExistingNonRoamEntriesArePreserved()
    {
        // Pre-populate launch.json with a user's hand-written config, then verify the emitter
        // doesn't clobber it.
        var temp = Directory.CreateTempSubdirectory("roam-attach-existing-");
        try
        {
            var launchPath = Path.Combine(temp.FullName, ".vscode", "launch.json");
            Directory.CreateDirectory(Path.GetDirectoryName(launchPath)!);
            await File.WriteAllTextAsync(launchPath, """
                {
                  "version": "0.2.0",
                  "configurations": [
                    {
                      "name": "Local Debug",
                      "type": "coreclr",
                      "request": "launch",
                      "program": "${workspaceFolder}/bin/Debug/net10.0/Kiosk.dll"
                    }
                  ]
                }
                """);

            await DebuggerEmitter.EmitAsync(
                launchPath, "kiosk", "C:/repos/kiosk-ui", "C:/repos/kiosk-ui", "/remote",
                Host(), Debug(), CancellationToken.None);

            var root = (JsonNode.Parse(await File.ReadAllTextAsync(launchPath)) as JsonObject)!;
            var configurations = (JsonArray)root["configurations"]!;
            var names = configurations.Select(n => ((JsonObject)n!)["name"]!.GetValue<string>()).ToArray();

            Assert.Contains("Local Debug", names);
            Assert.Contains("roam: kiosk", names);
            Assert.Equal(2, configurations.Count);
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
