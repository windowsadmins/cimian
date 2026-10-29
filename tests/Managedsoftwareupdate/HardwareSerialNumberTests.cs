using Xunit;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

public class HardwareSerialNumberTests
{
    [Fact]
    public void ValidBiosSerialIsTrimmedAndDoesNotReadMotherboard()
    {
        var boardQueried = false;
        var serial = HardwareSerialNumber.Resolve(() => "  BIOS-123  ", () =>
        {
            boardQueried = true;
            return "BOARD-123";
        });
        Assert.Equal("BIOS-123", serial);
        Assert.False(boardQueried);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Default String")]
    [InlineData("  dEfAuLt StRiNg  ")]
    [InlineData("System Serial Number")]
    [InlineData(" system serial number ")]
    public void InvalidBiosSerialUsesTrimmedMotherboardSerial(string? bios)
    {
        Assert.Equal("BOARD-123", HardwareSerialNumber.Resolve(() => bios, () => " BOARD-123 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" Default String ")]
    [InlineData(" system serial number ")]
    public void InvalidMotherboardSerialIsNotAnIdentity(string? board)
    {
        Assert.Null(HardwareSerialNumber.Resolve(() => "Default String", () => board));
    }

    [Fact]
    public void BiosReadFailureStillTriesMotherboard()
    {
        Assert.Equal("BOARD-123", HardwareSerialNumber.Resolve(
            () => throw new InvalidOperationException("BIOS unavailable"), () => "BOARD-123"));
    }

    [Fact]
    public void BothReadFailuresReturnNoIdentity()
    {
        Assert.Null(HardwareSerialNumber.Resolve(
            () => throw new InvalidOperationException("BIOS unavailable"),
            () => throw new InvalidOperationException("Motherboard unavailable")));
    }
}
