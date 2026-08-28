using Xunit;

namespace Roam.UnitTests;

public sealed class RuntimeCompatibilityTests
{
    [Theory]
    [InlineData("net10.0", 10, 0)]
    [InlineData("net10.0-windows", 10, 0)]
    [InlineData("net9.0", 9, 0)]
    [InlineData(" net10.0 ", 10, 0)]
    public void ParsesKnownFrameworkMonikers(string targetFramework, int major, int minor)
    {
        Assert.Equal(new Version(major, minor), RuntimeCompatibility.ParseTargetFrameworkVersion(targetFramework));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("netstandard2.0")]
    [InlineData("net48")]
    [InlineData("nonsense")]
    public void ReturnsNullForUnrecognizedMonikers(string? targetFramework)
    {
        Assert.Null(RuntimeCompatibility.ParseTargetFrameworkVersion(targetFramework));
    }

    [Fact]
    public void ParsesOnlyNetCoreAppRuntimesAndStripsPrerelease()
    {
        var output = string.Join('\n',
            @"Microsoft.AspNetCore.App 8.0.11 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]",
            @"Microsoft.NETCore.App 8.0.11 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]",
            @"Microsoft.NETCore.App 10.0.0-rc.1.25451.107 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]",
            @"Microsoft.WindowsDesktop.App 8.0.11 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]");

        Assert.Equal(
            new[] { new Version(8, 0, 11), new Version(10, 0, 0) },
            RuntimeCompatibility.ParseInstalledRuntimes(output));
    }

    [Fact]
    public void ParseInstalledRuntimesReturnsEmptyWhenNoNetCoreApp()
    {
        Assert.Empty(RuntimeCompatibility.ParseInstalledRuntimes("Microsoft.AspNetCore.App 8.0.11 [x]\n"));
    }

    [Fact]
    public void CompatibleWhenMatchingMajorMinorPresent()
    {
        var installed = new[] { new Version(8, 0, 11), new Version(9, 0, 2), new Version(10, 0, 0) };
        Assert.True(RuntimeCompatibility.IsCompatible(new Version(10, 0), installed));
    }

    [Fact]
    public void CompatibleViaMinorRollForwardWithinSameMajor()
    {
        Assert.True(RuntimeCompatibility.IsCompatible(new Version(10, 0), [new Version(10, 3, 1)]));
    }

    [Fact]
    public void IncompatibleWhenMajorMissing()
    {
        Assert.False(RuntimeCompatibility.IsCompatible(new Version(10, 0), [new Version(8, 0, 11), new Version(9, 0, 2)]));
    }

    [Fact]
    public void IncompatibleWhenMinorTooLow()
    {
        Assert.False(RuntimeCompatibility.IsCompatible(new Version(10, 1), [new Version(10, 0, 5)]));
    }

    // Default host roll-forward does not cross majors: a 10.0 app is not satisfied by an 11.0 runtime.
    [Fact]
    public void IncompatibleWhenOnlyHigherMajorPresent()
    {
        Assert.False(RuntimeCompatibility.IsCompatible(new Version(10, 0), [new Version(11, 0, 0)]));
    }

    [Fact]
    public void IncompatibleWhenNothingInstalled()
    {
        Assert.False(RuntimeCompatibility.IsCompatible(new Version(10, 0), []));
    }

    [Theory]
    [InlineData("win-x64", "windows")]
    [InlineData("win10-x64", "windows")]   // legacy specific RID
    [InlineData("WIN-ARM64", "windows")]   // case-insensitive
    [InlineData("linux-x64", "linux")]
    [InlineData("linux-musl-arm64", "linux")]
    [InlineData("osx-arm64", "macos")]
    [InlineData("osx.13-x64", "macos")]
    public void RidOperatingSystem_MapsKnownFamilies(string rid, string expected)
        => Assert.Equal(expected, RuntimeCompatibility.RidOperatingSystem(rid));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("freebsd-x64")]   // a family roam doesn't deploy to -> don't block
    [InlineData("browser-wasm")]
    public void RidOperatingSystem_NullForUnknownFamilies(string? rid)
        => Assert.Null(RuntimeCompatibility.RidOperatingSystem(rid));

    // A confident RID/target-OS mismatch must produce an error message (the silent footgun:
    // win-x64 shipped to a Linux target). The message names both sides and suggests a fix.
    [Theory]
    [InlineData("win-x64", "linux")]
    [InlineData("linux-x64", "windows")]
    [InlineData("osx-arm64", "linux")]
    public void ValidatePublishOsTargetsHost_FlagsMismatch(string rid, string targetOs)
    {
        var message = RuntimeCompatibility.ValidatePublishOsTargetsHost(rid, targetOs);
        Assert.NotNull(message);
        Assert.Contains(rid, message);
        Assert.Contains(targetOs, message);
    }

    // No false positives: matching OS, unknown RID family, or unknown/absent target OS all pass.
    [Theory]
    [InlineData("win-x64", "windows")]
    [InlineData("linux-musl-x64", "linux")]
    [InlineData("osx-arm64", "macos")]
    [InlineData("freebsd-x64", "linux")]   // unknown RID family -> fail-open
    [InlineData("win-x64", null)]          // unknown target OS -> fail-open
    [InlineData(null, "linux")]            // no RID (portable framework-dependent) -> nothing to check
    public void ValidatePublishOsTargetsHost_NullWhenNothingToFlag(string? rid, string? targetOs)
        => Assert.Null(RuntimeCompatibility.ValidatePublishOsTargetsHost(rid, targetOs));
}
