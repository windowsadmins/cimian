using System.CommandLine;
using Xunit;

namespace Cimian.Tests.CLI;

/// <summary>
/// Parses representative command lines for each System.CommandLine tool without running
/// them, so a library upgrade that changes how a line is read fails here.
/// </summary>
public class CommandLineParsingTests
{
    private static ParseResult Parse(RootCommand root, params string[] args) => root.Parse(args);

    private static ParseResult ParseOk(RootCommand root, params string[] args)
    {
        var result = root.Parse(args);
        Assert.True(result.Errors.Count == 0, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result;
    }

    private static void AssertError(RootCommand root, params string[] args)
        => Assert.NotEmpty(Parse(root, args).Errors);

    // --- makecatalogs ------------------------------------------------------------------

    [Theory]
    [InlineData("--repo_path")]
    [InlineData("-repo_path")]
    [InlineData("-r")]
    public void Makecatalogs_RepoPathAliases(string alias)
    {
        var result = ParseOk(Cimian.CLI.Makecatalogs.Program.CreateRootCommand(), alias, @"C:\repo");
        Assert.Equal(@"C:\repo", result.GetValue<string?>("--repo_path"));
    }

    [Fact]
    public void Makecatalogs_FlagsAndBundledShortFlags()
    {
        var root = Cimian.CLI.Makecatalogs.Program.CreateRootCommand();
        var result = ParseOk(root, "-r", "R", "-sq", "--hash_check", "--tolerate_parse_errors");
        Assert.True(result.GetValue<bool>("--skip_payload_check"));
        Assert.True(result.GetValue<bool>("--silent"));
        Assert.True(result.GetValue<bool>("--hash_check"));
        Assert.True(result.GetValue<bool>("--tolerate_parse_errors"));
        Assert.True(ParseOk(root, "-V").GetValue<bool>("-V"));
        Assert.False(ParseOk(root).GetValue<bool>("--silent"));
    }

    [Fact]
    public void Makecatalogs_RejectsUnknownOptionsExtraTokensAndMissingValues()
    {
        var root = Cimian.CLI.Makecatalogs.Program.CreateRootCommand();
        AssertError(root, "--bogus");
        AssertError(root, "-r", "R", "extra");
        AssertError(root, "--repo_path");
    }

    // --- repoclean -----------------------------------------------------------------------

    [Fact]
    public void Repoclean_DefaultsAndAliases()
    {
        var root = Cimian.CLI.Repoclean.Program.CreateRootCommand();
        Assert.Equal(2, ParseOk(root, "-r", "R").GetValue<int>("--keep"));

        var result = ParseOk(root, "--repo-url", "R", "-k", "5", "-a", "-y", "--delete");
        Assert.Equal("R", result.GetValue<string?>("--repo-url"));
        Assert.Equal(5, result.GetValue<int>("--keep"));
        Assert.True(result.GetValue<bool>("--show-all"));
        Assert.True(result.GetValue<bool>("--auto"));
        Assert.True(result.GetValue<bool>("--remove"));
    }

    [Fact]
    public void Repoclean_RejectsANonNumericKeep()
    {
        var root = Cimian.CLI.Repoclean.Program.CreateRootCommand();
        AssertError(root, "-r", "R", "-k", "abc");
        AssertError(root, "-r", "R", "--keep");
    }

    // --- manifestutil -------------------------------------------------------------------

    [Fact]
    public void Manifestutil_DefaultsAndOptions()
    {
        var root = Cimian.CLI.Manifestutil.Program.CreateRootCommand();
        var defaults = ParseOk(root);
        Assert.Equal("managed_installs", defaults.GetValue<string>("--section"));
        Assert.Equal(Cimian.Core.CimianPaths.ConfigYaml, defaults.GetValue<string>("--config"));

        var result = ParseOk(root, "-c", "cfg.yaml", "-m", "site_default", "-a", "Firefox", "-s", "optional_installs");
        Assert.Equal("cfg.yaml", result.GetValue<string>("--config"));
        Assert.Equal("site_default", result.GetValue<string?>("--manifest"));
        Assert.Equal("Firefox", result.GetValue<string?>("--add-pkg"));
        Assert.Equal("optional_installs", result.GetValue<string>("--section"));

        Assert.True(ParseOk(root, "-l").GetValue<bool>("--list-manifests"));
        Assert.Equal("X", ParseOk(root, "--selfservice-request", "X").GetValue<string?>("--selfservice-request"));
        Assert.Equal("Y", ParseOk(root, "-n", "Y").GetValue<string?>("--new-manifest"));
        Assert.Equal("Z", ParseOk(root, "-r", "Z").GetValue<string?>("--remove-pkg"));
    }

    [Fact]
    public void Manifestutil_RejectsExtraTokens()
    {
        var root = Cimian.CLI.Manifestutil.Program.CreateRootCommand();
        AssertError(root, "-c", "cfg.yaml", "extra");
        AssertError(root, "-c");
    }

    // --- makepkginfo ----------------------------------------------------------------------

    [Fact]
    public void Makepkginfo_InstallerFilesAndFlags()
    {
        var root = Cimian.CLI.Makepkginfo.Program.CreateRootCommand();
        Assert.Equal("Development", ParseOk(root).GetValue<string>("--catalogs"));

        var result = ParseOk(root, "--catalogs", "Testing,Production", "--OnDemand", "--unattended_install",
            "-f", "a.exe", "--file", "b.exe", "--unused_removal_days", "30", "--pkg-version", "1.0", "app.msi");
        Assert.Equal("Testing,Production", result.GetValue<string>("--catalogs"));
        Assert.True(result.GetValue<bool>("--OnDemand"));
        Assert.True(result.GetValue<bool>("--unattended_install"));
        Assert.Equal(new[] { "a.exe", "b.exe" }, result.GetValue<string[]>("--file"));
        Assert.Equal(30, result.GetValue<int?>("--unused_removal_days"));
        Assert.Equal("1.0", result.GetValue<string?>("--pkg-version"));
        Assert.Equal("app.msi", result.GetValue<string?>("installer"));
        Assert.Null(ParseOk(root, "--new").GetValue<string?>("installer"));
    }

    [Fact]
    public void Makepkginfo_RejectsBadValuesAndASecondInstaller()
    {
        var root = Cimian.CLI.Makepkginfo.Program.CreateRootCommand();
        AssertError(root, "--unused_removal_days", "abc", "x.msi");
        AssertError(root, "a.msi", "b.msi");
        AssertError(root, "--catalogs");
    }

    [Fact]
    public void Makepkginfo_UnknownDoubleDashTokenIsTakenAsTheInstaller()
    {
        // As on beta4: the optional installer argument takes it, and the handler then
        // reports "Installer file not found: --ondemand".
        var result = ParseOk(Cimian.CLI.Makepkginfo.Program.CreateRootCommand(), "--ondemand");
        Assert.Equal("--ondemand", result.GetValue<string?>("installer"));
    }

    // --- cimiimport --------------------------------------------------------------------------

    [Fact]
    public void Cimiimport_InstallerAndOptions()
    {
        var root = Cimian.CLI.Cimiimport.Program.CreateRootCommand();
        Assert.Null(ParseOk(root).GetValue<string?>("installerPath"));

        var result = ParseOk(root, "app.msi", "-i", "a.exe", "--installs-array", "b.exe", "--arch", "x64",
            "--repo_path", "R", "--nointeractive", "--emit-installs");
        Assert.Equal("app.msi", result.GetValue<string?>("installerPath"));
        Assert.Equal(new[] { "a.exe", "b.exe" }, result.GetValue<string[]>("--installs-array"));
        Assert.Equal("x64", result.GetValue<string?>("--arch"));
        Assert.Equal("R", result.GetValue<string?>("--repo_path"));
        Assert.True(result.GetValue<bool>("--nointeractive"));
        Assert.True(result.GetValue<bool>("--emit-installs"));
    }

    [Fact]
    public void Cimiimport_RejectsASecondInstallerAndMissingValues()
    {
        var root = Cimian.CLI.Cimiimport.Program.CreateRootCommand();
        AssertError(root, "a.msi", "b.msi");
        AssertError(root, "--repo_path");
    }

    [Fact]
    public void Cimiimport_UnknownDoubleDashTokenIsTakenAsTheInstaller()
    {
        // As on beta4: the optional installerPath argument takes it.
        var result = ParseOk(Cimian.CLI.Cimiimport.Program.CreateRootCommand(), "--bogus");
        Assert.Equal("--bogus", result.GetValue<string?>("installerPath"));
    }

    // --- cimistatus ---------------------------------------------------------------------------

    [Theory]
    [InlineData("service", "service")]
    [InlineData("logs", "logs")]
    [InlineData("config", "config")]
    [InlineData("diag", "diag")]
    [InlineData("diagnostics", "diag")]
    public void Cimistatus_Subcommands(string token, string command)
        => Assert.Equal(command, ParseOk(Cimian.CLI.Cimistatus.Program.CreateRootCommand(), token).CommandResult.Command.Name);

    [Fact]
    public void Cimistatus_LogsOpenAndErrors()
    {
        var root = Cimian.CLI.Cimistatus.Program.CreateRootCommand();
        Assert.True(ParseOk(root, "logs", "-o").GetValue<bool>("--open"));
        Assert.False(ParseOk(root, "logs").GetValue<bool>("--open"));
        AssertError(root, "servce");
        AssertError(root, "logs", "--bogus");
    }

    // --- cimitrigger ---------------------------------------------------------------------------

    [Theory]
    [InlineData("gui")]
    [InlineData("headless")]
    [InlineData("debug")]
    public void Cimitrigger_Subcommands(string command)
        => Assert.Equal(command, ParseOk(CimianTools.CimiTrigger.Program.CreateRootCommand(), command).CommandResult.Command.Name);

    [Fact]
    public void Cimitrigger_ForceTakesAMode()
    {
        var root = CimianTools.CimiTrigger.Program.CreateRootCommand();
        var result = ParseOk(root, "--force", "headless");
        Assert.Equal("--force", result.CommandResult.Command.Name);
        Assert.Equal("headless", result.GetValue<string>("mode"));
        AssertError(root, "--force");
        AssertError(root, "gui", "extra");
    }

    // --- cimiwatcher -----------------------------------------------------------------------------

    [Theory]
    [InlineData("install")]
    [InlineData("remove")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("continue")]
    [InlineData("status")]
    [InlineData("debug")]
    [InlineData("service")]
    public void Cimiwatcher_Subcommands(string command)
        => Assert.Equal(command, ParseOk(Cimian.CLI.Cimiwatcher.Program.CreateRootCommand(), command).CommandResult.Command.Name);

    [Fact]
    public void Cimiwatcher_ServiceIsHiddenAndExtraTokensFail()
    {
        var root = Cimian.CLI.Cimiwatcher.Program.CreateRootCommand();
        Assert.True(root.Subcommands.Single(c => c.Name == "service").Hidden);
        AssertError(root, "status", "extra");
        AssertError(root, "instal");
    }
}
