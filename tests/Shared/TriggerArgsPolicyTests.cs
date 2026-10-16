using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

public class TriggerArgsPolicyTests
{
    [Theory]
    [InlineData("--checkonly --show-status -vv --status-port 19848")]
    [InlineData("--installonly --show-status -vv --status-port 19848")]
    [InlineData("--auto --show-status -vv")]
    [InlineData("--auto")]
    [InlineData("--item Firefox --no-preflight --show-status -vv --status-port 19848")]
    [InlineData("--item Firefox \"VS Code\" --no-preflight --show-status -vv")]
    public void TrySanitize_AcceptsWhatTheGuiAndServiceWrite(string args)
    {
        Assert.True(TriggerArgsPolicy.TrySanitize(args, out var sanitized, out var reason), reason);
        Assert.Equal(args, sanitized);
    }

    [Fact]
    public void TrySanitize_RoundTripsSelfServeArgsBuiltByTheGui()
    {
        var args = $"{BootstrapArgsBuilder.BuildSelfServeInstallArgs(new[] { "Firefox", "VS Code", "trailing\\" })} --status-port 19848";

        Assert.True(TriggerArgsPolicy.TrySanitize(args, out var sanitized, out _));
        Assert.Equal(
            new[] { "Firefox", "VS Code", "trailing\\" },
            BootstrapArgsBuilder.ExtractItemNames(sanitized));
    }

    [Theory]
    [InlineData("--auto --config C:\\Users\\Public\\evil.yaml")]
    [InlineData("--auto --config=C:\\Users\\Public\\evil.yaml")]
    [InlineData("--local-only-manifest C:\\Users\\Public\\evil.yaml")]
    [InlineData("--auto -m SomeOtherManifest")]
    [InlineData("--auto --manifest SomeOtherManifest")]
    [InlineData("--clear-loop all")]
    [InlineData("--perform-selfupdate")]
    [InlineData("--set-bootstrap-mode")]
    [InlineData("--auto --no-postflight")]
    [InlineData("--auto --no-preflight")]
    [InlineData("--preflight-only")]
    [InlineData("--auto --unknown-flag")]
    [InlineData("--auto stray")]
    [InlineData("--item")]
    [InlineData("--item Firefox --item Chrome")]
    [InlineData("--auto --status-port")]
    [InlineData("--auto --status-port notaport")]
    [InlineData("--auto --status-port 0")]
    [InlineData("--auto --status-port 70000")]
    [InlineData("-ac")]
    public void TrySanitize_RejectsAnythingOutsideTheAllowList(string args)
    {
        Assert.False(TriggerArgsPolicy.TrySanitize(args, out var sanitized, out var reason));
        Assert.Null(sanitized);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TrySanitize_RejectsItemNameThatLooksLikeASwitchAfterUnquoting()
    {
        // A quoted token is still a switch to the engine's parser once the quotes are gone.
        Assert.False(TriggerArgsPolicy.TrySanitize("--item \"--config\" C:\\evil.yaml", out _, out _));
    }

    [Fact]
    public void TrySanitize_RejectsEmptyArgs()
    {
        Assert.False(TriggerArgsPolicy.TrySanitize("   ", out _, out _));
    }
}
