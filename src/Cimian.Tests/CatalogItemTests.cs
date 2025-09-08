using Xunit;
using FluentAssertions;
using Cimian.Core.Models;

namespace Cimian.Tests.Core.Models;

/// <summary>
/// Tests for the CatalogItem model
/// </summary>
public class CatalogItemTests
{
    [Fact]
    public void CatalogItem_ShouldHaveRequiredProperties()
    {
        // Arrange & Act
        var item = new CatalogItem
        {
            Name = "test-package",
            Version = "1.0.0"
        };

        // Assert
        item.Name.Should().Be("test-package");
        item.Version.Should().Be("1.0.0");
        item.RequiresElevation.Should().BeTrue(); // Default value
    }

    [Fact]
    public void GetDisplayName_ShouldReturnDisplayNameWhenSet()
    {
        // Arrange
        var item = new CatalogItem
        {
            Name = "test-package",
            DisplayName = "Test Package"
        };

        // Act
        var displayName = item.GetDisplayName();

        // Assert
        displayName.Should().Be("Test Package");
    }

    [Fact]
    public void GetDisplayName_ShouldReturnNameWhenDisplayNameNotSet()
    {
        // Arrange
        var item = new CatalogItem
        {
            Name = "test-package"
        };

        // Act
        var displayName = item.GetDisplayName();

        // Assert
        displayName.Should().Be("test-package");
    }

    [Fact]
    public void GetPackageId_ShouldReturnNameVersionCombination()
    {
        // Arrange
        var item = new CatalogItem
        {
            Name = "test-package",
            Version = "1.2.3"
        };

        // Act
        var packageId = item.GetPackageId();

        // Assert
        packageId.Should().Be("test-package-1.2.3");
    }

    [Fact]
    public void SupportsArchitecture_ShouldReturnTrueWhenArchitectureSupported()
    {
        // Arrange
        var item = new CatalogItem
        {
            SupportedArchitectures = new List<string> { "x64", "arm64" }
        };

        // Act & Assert
        item.SupportsArchitecture("x64").Should().BeTrue();
        item.SupportsArchitecture("arm64").Should().BeTrue();
        item.SupportsArchitecture("x86").Should().BeFalse();
    }

    [Fact]
    public void SupportsArchitecture_ShouldReturnTrueWhenNoArchitecturesSpecified()
    {
        // Arrange
        var item = new CatalogItem();

        // Act & Assert
        item.SupportsArchitecture("x64").Should().BeTrue();
        item.SupportsArchitecture("any").Should().BeTrue();
    }
}
