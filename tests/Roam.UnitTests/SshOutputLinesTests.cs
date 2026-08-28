using Roam;
using Xunit;

namespace Roam.UnitTests;

// Locks the pure line-selection core (SshOutputLines) that decides which line of a captured
// stdout/stderr blob the user sees. The motivating regression (charles8051/roam#7): over SSH,
// stderr opens with benign warnings (missing identity file, no-pty notice, OpenSSH post-quantum
// advisory) before the real error -- naive "first non-empty line" surfaced the warning and masked
// the failure. Every case below feeds a benign-warning preamble + a real error and asserts the
// real error wins.
public sealed class SshOutputLinesTests
{
    // The exact benign lines OpenSSH prints over a non-interactive channel, verbatim from the
    // field reports in the issue.
    private const string IdentityWarning =
        "Warning: Identity file /c/Users/dev/.ssh/id_rsa not accessible: No such file or directory.";
    private const string PseudoTerminalWarning =
        "Pseudo-terminal will not be allocated because stdin is not a terminal.";
    private const string PermanentlyAddedWarning =
        "Warning: Permanently added 'kiosk' (ED25519) to the list of known hosts.";
    private const string PqWarning1 =
        "** WARNING: connection is not using a post-quantum key exchange algorithm.";
    private const string PqWarning2 =
        "** This session may be vulnerable to a store-now-decrypt-later quantum attack.";
    private const string PqWarning3 =
        "** See https://openssh.com/pq.html for more information.";

    private const string RealError = "Register-ScheduledTask : Access is denied";

    public static TheoryData<string> BenignWarnings =>
    [
        IdentityWarning,
        PseudoTerminalWarning,
        PermanentlyAddedWarning,
        PqWarning1,
        PqWarning2,
        PqWarning3,
    ];

    [Theory]
    [MemberData(nameof(BenignWarnings))]
    public void IsBenignSshNoise_KnownWarnings_AreNoise(string warning)
    {
        Assert.True(SshOutputLines.IsBenignSshNoise(warning));
    }

    [Theory]
    [MemberData(nameof(BenignWarnings))]
    public void BestError_WarningThenRealError_SurfacesRealError(string warning)
    {
        var stderr = $"{warning}\n{RealError}";

        Assert.Equal(RealError, SshOutputLines.BestError(stderr));
    }

    [Theory]
    [MemberData(nameof(BenignWarnings))]
    public void FirstMeaningful_WarningThenRealError_SkipsWarning(string warning)
    {
        var stderr = $"{warning}\n{RealError}";

        Assert.Equal(RealError, SshOutputLines.FirstMeaningful(stderr));
    }

    // The full preamble OpenSSH emits before a failing remote command -- identity warning, the
    // entire post-quantum advisory block, then the real error ~700 bytes deep. This is the exact
    // shape that produced the unhelpful `exit=7 step=start` with only the id_rsa warning shown.
    [Fact]
    public void BestError_FullSshPreambleThenError_SurfacesError()
    {
        var stderr = string.Join('\n',
            IdentityWarning,
            PqWarning1,
            PqWarning2,
            PqWarning3,
            PseudoTerminalWarning,
            RealError);

        Assert.Equal(RealError, SshOutputLines.BestError(stderr));
    }

    // A real error buried *between* warnings (not last) must still win over the trailing benign
    // line -- the error-marker scan, not the last-line fallback, has to catch it.
    [Fact]
    public void BestError_RealErrorBetweenWarnings_PrefersError()
    {
        var stderr = string.Join('\n', IdentityWarning, RealError, PqWarning1);

        Assert.Equal(RealError, SshOutputLines.BestError(stderr));
    }

    // Banner-then-error: dotnet/msbuild open with non-error chatter, so the *last* meaningful line
    // is the better fallback than the first. (Locks the original BestErrorLine intent.)
    [Fact]
    public void BestError_BannerThenErrorKeyword_PrefersErrorLine()
    {
        var text = "Determining projects to restore...\nerror NU1101: package not found\nBuild FAILED.";

        Assert.Equal("error NU1101: package not found", SshOutputLines.BestError(text));
    }

    // When nothing carries an error marker, fall back to the last meaningful line.
    [Fact]
    public void BestError_NoErrorMarker_ReturnsLastMeaningfulLine()
    {
        var text = $"{IdentityWarning}\nfirst real line\nsecond real line";

        Assert.Equal("second real line", SshOutputLines.BestError(text));
    }

    // Degenerate case: if every line is benign noise we still return something rather than null,
    // so a non-zero exit with only-warning output isn't reported as an empty reason. With no
    // meaningful line to find, BestError keeps its last-line fallback.
    [Fact]
    public void BestError_AllBenign_FallsBackToLastLine()
    {
        var stderr = $"{IdentityWarning}\n{PseudoTerminalWarning}";

        Assert.Equal(PseudoTerminalWarning, SshOutputLines.BestError(stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Selectors_EmptyOrNull_ReturnNull(string? text)
    {
        Assert.Null(SshOutputLines.BestError(text));
        Assert.Null(SshOutputLines.FirstMeaningful(text));
    }

    [Fact]
    public void IsBenignSshNoise_GenuineError_IsNotNoise()
    {
        Assert.False(SshOutputLines.IsBenignSshNoise(RealError));
        Assert.False(SshOutputLines.IsBenignSshNoise("Start-Process : The system cannot find the file specified."));
    }

    // False-negative guard: a real error line that merely mentions a benign keyword must NOT be
    // classified as noise. The two-token anchoring (e.g. "Identity file" AND "not accessible") is
    // what prevents these from being swallowed.
    [Theory]
    [InlineData("Identity file successfully loaded from the agent.")]
    [InlineData("Permanently added the user to the allowlist but the write failed.")]
    [InlineData("Pseudo-terminal session established, then the handshake was refused.")]
    public void IsBenignSshNoise_RealLineMentioningKeyword_IsNotNoise(string line)
    {
        Assert.False(SshOutputLines.IsBenignSshNoise(line));
    }

    // Every error marker must be exercised, so a future edit that drops one is caught. Each case
    // is a benign preamble + a line carrying exactly that marker -- BestError must surface it.
    [Theory]
    [InlineData("ssh: error: unable to authenticate")]
    [InlineData("fatal: unable to access the repository")]
    [InlineData("System.UnauthorizedAccessException: access to the path is denied")]
    [InlineData("connect: connection refused")]
    [InlineData("The term 'frob' is not recognized as a cmdlet")]
    [InlineData("Login failure: unauthorized credentials")]
    public void BestError_MarkerLineAfterWarning_IsSurfaced(string errorLine)
    {
        var stderr = $"{IdentityWarning}\n{errorLine}";

        Assert.Equal(errorLine, SshOutputLines.BestError(stderr));
    }

    // FirstMeaningful's documented all-benign fallback: when every line is noise it returns the
    // first line rather than null, so a non-zero exit with only-warning output still reports
    // something. (BestError's analogue is covered above; this locks FirstMeaningful's.)
    [Fact]
    public void FirstMeaningful_AllBenign_FallsBackToFirstLine()
    {
        var stderr = $"{IdentityWarning}\n{PseudoTerminalWarning}";

        Assert.Equal(IdentityWarning, SshOutputLines.FirstMeaningful(stderr));
    }

    // Windows-sourced SSH output is CRLF-delimited; SplitLines relies on TrimEntries to strip the
    // trailing \r. A regression that drops TrimEntries would corrupt the matching -- lock it.
    [Fact]
    public void BestError_CrlfLineEndings_SurfacesRealError()
    {
        var stderr = $"{IdentityWarning}\r\n{PqWarning1}\r\n{RealError}\r\n";

        Assert.Equal(RealError, SshOutputLines.BestError(stderr));
    }

    // FirstMeaningful shares SplitLines/TrimEntries with BestError; assert CRLF handling on this
    // path too so the contract is symmetric across both selectors.
    [Fact]
    public void FirstMeaningful_CrlfLineEndings_SkipsWarning()
    {
        var stderr = $"{IdentityWarning}\r\n{RealError}\r\n";

        Assert.Equal(RealError, SshOutputLines.FirstMeaningful(stderr));
    }
}
