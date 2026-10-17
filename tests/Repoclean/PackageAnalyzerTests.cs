using Cimian.CLI.Repoclean.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cimian.Tests.Repoclean;

public class PackageAnalyzerTests
{
    [Theory]
    [InlineData("/mgmt/Tool.msi", @"mgmt\Tool.msi")]
    [InlineData(@"\mgmt\Tool.msi", "mgmt/Tool.msi")]
    [InlineData("mgmt/TOOL.MSI", @"MGMT\Tool.msi")]
    public async Task ReferencedPayload_WithDifferentSeparatorsOrCase_IsNotAnOrphan(
        string referencedPath, string listedPath)
    {
        var repository = new Mock<IFileRepository>();
        repository.Setup(r => r.GetItemListAsync("pkgs"))
            .ReturnsAsync(new[] { listedPath, @"mgmt\Unreferenced.msi" });
        var analyzer = new PackageAnalyzer(NullLogger<PackageAnalyzer>.Instance);

        var orphans = await analyzer.FindOrphanedPackagesAsync(repository.Object,
            new HashSet<string> { referencedPath });

        Assert.Equal(new[] { "mgmt/Unreferenced.msi" }, orphans);
        repository.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }
}
