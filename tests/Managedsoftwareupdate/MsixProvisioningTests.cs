using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

public class MsixProvisioningTests
{
    [Theory]
    [InlineData("installed", true, "OK|Example.App_1.4.7.0_x64__publisher")]
    [InlineData("multiple", true, "OK|Example.App_1.4.7.0_x64__publisher")]
    [InlineData("existing", true, "SKIP|Example.App_2.0.0.0_x64__publisher")]
    [InlineData("missing", false, "ERROR|No provisioned package found")]
    [InlineData("wrong-identity", false, "ERROR|No provisioned package found")]
    [InlineData("empty-name", false, "ERROR|No provisioned package found")]
    [InlineData("old-version", false, "ERROR|Provisioned version")]
    [InlineData("install-failed", false, "ERROR|Fixture provisioning failed")]
    [InlineData("query-failed", false, "ERROR|Fixture query failed")]
    public async Task InstallScript_ResolvesProvisionedIdentityInsteadOfDismImageResult(
        string mode, bool expectedSuccess, string expectedMarker)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CimianMsixTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var item = new CatalogItem
            {
                Name = "Example App",
                Version = "1.4.7.0",
                Installs = [new InstallCheckItem { Type = "msix", IdentityName = "Example.App" }]
            };
            var script = InstallerService.BuildMsixInstallScript(item,
                Path.Combine(directory, "package's file.msix"), Path.Combine(directory, "install's log.txt"));
            var fixture = "$script:Mode = '" + mode + "'\n" + """
                $script:Added = $false
                function Get-AppxPackage {
                    [CmdletBinding()] param([switch]$AllUsers, [string]$Name)
                }
                function Remove-AppxPackage {
                    [CmdletBinding()] param([switch]$AllUsers)
                    throw 'No real Appx cmdlet may run in this fixture'
                }
                function Add-AppxProvisionedPackage {
                    [CmdletBinding()] param([switch]$Online, [string]$PackagePath, [switch]$SkipLicense)
                    if ($script:Mode -eq 'existing') { throw 'An existing newer package must not be provisioned' }
                    if ($script:Mode -eq 'install-failed') { throw 'Fixture provisioning failed' }
                    if ($PackagePath -notlike "*package's file.msix") { throw 'Package path quoting changed' }
                    $script:Added = $true
                    [pscustomobject]@{ Path = ''; Online = $true; RestartNeeded = $false }
                }
                function Get-AppxProvisionedPackage {
                    [CmdletBinding()] param([switch]$Online)
                    if (-not $script:Added -and $script:Mode -ne 'existing') { return }
                    if ($script:Mode -eq 'query-failed') { throw 'Fixture query failed' }
                    if ($script:Mode -eq 'missing') { return }
                    $version = if ($script:Mode -eq 'old-version') { '1.0.0.0' } elseif ($script:Mode -eq 'existing') { '2.0.0.0' } else { '1.4.7.0' }
                    $identity = if ($script:Mode -eq 'wrong-identity') { 'Unrelated.App' } else { 'Example.App' }
                    $name = if ($script:Mode -eq 'empty-name') { '' } else { "${identity}_${version}_x64__publisher" }
                    if ($script:Mode -eq 'multiple') {
                        [pscustomobject]@{ DisplayName = 'Unrelated.App'; Version = '9.0.0.0'; PackageName = 'Unrelated.App_9.0.0.0_x64__publisher' }
                        [pscustomobject]@{ DisplayName = 'Example.App'; Version = '1.0.0.0'; PackageName = 'Example.App_1.0.0.0_x64__publisher' }
                    }
                    [pscustomobject]@{ DisplayName = $identity; Version = $version; PackageName = $name }
                }
                """;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await new ScriptService().ExecuteScriptWithDetailsAsync(fixture + "\n" + script, timeout.Token);

            Assert.Equal(expectedSuccess, result.Success);
            Assert.Contains(expectedMarker, result.Output);
            if (!expectedSuccess)
                Assert.DoesNotContain("OK|", result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
