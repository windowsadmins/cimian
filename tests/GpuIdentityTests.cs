using FluentAssertions;
using Cimian.Infrastructure.System;
using Xunit;

namespace Cimian.Tests;

/// <summary>
/// Tests for driver-independent GPU identity parsing.
///
/// These cover the path that matters when a vendor driver is missing: Windows stops
/// reporting a model name, so the PCI hardware ID is the only thing left to identify
/// the card by.
/// </summary>
public class GpuIdentityTests
{
    [Theory]
    // Full instance ID as Win32_VideoController reports it
    [InlineData(@"PCI\VEN_10DE&DEV_24B0&SUBSYS_14AD10DE&REV_A1\4&2C4C8C34&0&0008", @"PCI\VEN_10DE&DEV_24B0")]
    // Already normalized
    [InlineData(@"PCI\VEN_10DE&DEV_24B0", @"PCI\VEN_10DE&DEV_24B0")]
    // Lower case from the PnP enumerator
    [InlineData(@"pci\ven_8086&dev_3e92&subsys_86941043&rev_00\3&11583659&0&10", @"PCI\VEN_8086&DEV_3E92")]
    // Not a PCI device
    [InlineData(@"USB\VID_045E&PID_07A5\6&38D62F2A&0&2", "")]
    // PCI device with no device ID
    [InlineData(@"PCI\VEN_10DE", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizePciId_ReducesToStableHardwareIdentity(string? instanceId, string expected)
    {
        GpuIdentity.NormalizePciId(instanceId).Should().Be(expected);
    }

    [Fact]
    public void NormalizePciId_IsStableAcrossSlotsForIdenticalCards()
    {
        // Two of the same card differ only by instance suffix, so both must normalize
        // to the same cache key.
        var first = GpuIdentity.NormalizePciId(@"PCI\VEN_10DE&DEV_24B0&SUBSYS_14AD10DE&REV_A1\4&2C4C8C34&0&0008");
        var second = GpuIdentity.NormalizePciId(@"PCI\VEN_10DE&DEV_24B0&SUBSYS_14AD10DE&REV_A1\4&2C4C8C34&0&0010");

        second.Should().Be(first);
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_24B0", "NVIDIA")]
    [InlineData(@"PCI\VEN_1002&DEV_73FF", "AMD")]
    [InlineData(@"PCI\VEN_8086&DEV_3E92", "Intel")]
    [InlineData(@"PCI\VEN_FFFF&DEV_0001", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void VendorFromPciId_ResolvesKnownVendors(string? pciId, string expected)
    {
        GpuIdentity.VendorFromPciId(pciId).Should().Be(expected);
    }

    [Theory]
    // Fallbacks Windows uses when no vendor driver is bound
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("3D Video Controller", true)]
    [InlineData("Video Controller (VGA Compatible)", true)]
    [InlineData("Standard VGA Graphics Adapter", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    // Real model names a driver supplies
    [InlineData("NVIDIA RTX A4000", false)]
    [InlineData("NVIDIA Quadro RTX 5000", false)]
    [InlineData("NVIDIA GeForce RTX 2060", false)]
    [InlineData("Intel(R) UHD Graphics 630", false)]
    [InlineData("AMD Radeon Pro W6600", false)]
    public void IsGenericAdapterName_SeparatesPlaceholdersFromModels(string? name, bool expected)
    {
        GpuIdentity.IsGenericAdapterName(name).Should().Be(expected);
    }
}
