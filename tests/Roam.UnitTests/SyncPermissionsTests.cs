using Xunit;

namespace Roam.UnitTests;

// Regression tests for the cross-platform exec-bit logic. The bug: deploying from a Windows
// controller to a Linux target skipped chmod entirely (GetUploadMode returned null whenever the
// controller was Windows), so the published apphost landed non-executable and `start` failed with
// permission-denied. The fix infers the exec bit from the file's shape when the controller can't
// read a real Unix mode.
public sealed class SyncPermissionsTests
{
    [Theory]
    [InlineData("MyApp")]            // self-contained apphost (extensionless ELF)
    [InlineData("createdump")]       // shipped alongside the runtime
    [InlineData("singlefilehost")]
    [InlineData("hooks/start.sh")]   // shell hook
    [InlineData("START.SH")]         // case-insensitive
    public void LooksExecutableOnUnix_True_ForApphostsAndScripts(string path)
        => Assert.True(SftpUploadPermissions.LooksExecutableOnUnix(path));

    [Theory]
    [InlineData("MyApp.dll")]
    [InlineData("MyApp.deps.json")]
    [InlineData("MyApp.runtimeconfig.json")]
    [InlineData("MyApp.pdb")]
    [InlineData("libSkiaSharp.so")]
    [InlineData("appsettings.json")]
    public void LooksExecutableOnUnix_False_ForDataFiles(string path)
        => Assert.False(SftpUploadPermissions.LooksExecutableOnUnix(path));

    // A Windows target ignores Unix modes regardless of controller OS — must stay null so roam
    // never issues a meaningless chmod against a Windows SFTP server.
    [Fact]
    public void GetUploadMode_WindowsTarget_IsAlwaysNull()
    {
        Assert.Null(SftpUploadPermissions.GetUploadMode("MyApp", windowsTarget: true));
        Assert.Null(SftpUploadPermissions.GetUploadMode("MyApp.dll", windowsTarget: true));
    }

    // The headline regression: on a Windows controller, a Unix target must still get a mode
    // (apphost executable, data file not) — not the pre-fix null that skipped chmod and shipped a
    // non-executable apphost. Meaningful only on a Windows controller.
    [Fact]
    public void GetUploadMode_UnixTargetFromWindowsController_InfersExecBit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal((short?)755, SftpUploadPermissions.GetUploadMode("MyApp", windowsTarget: false));
        Assert.Equal((short?)644, SftpUploadPermissions.GetUploadMode("MyApp.dll", windowsTarget: false));
    }

    // The unchanged Unix-controller path: mirror the source file's real exec bit. Meaningful only
    // on a Unix controller (File.SetUnixFileMode/GetUnixFileMode throw on Windows).
    [Fact]
    public void GetUploadMode_UnixTargetFromUnixController_MirrorsSourceMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("roam-perm-");
        try
        {
            var exe = Path.Combine(dir.FullName, "apphost");
            File.WriteAllText(exe, "#!/bin/sh\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal((short?)755, SftpUploadPermissions.GetUploadMode(exe, windowsTarget: false));

            var data = Path.Combine(dir.FullName, "data.dll");
            File.WriteAllText(data, "x");
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal((short?)644, SftpUploadPermissions.GetUploadMode(data, windowsTarget: false));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
