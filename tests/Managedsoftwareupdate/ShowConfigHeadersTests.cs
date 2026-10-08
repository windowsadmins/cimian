using Cimian.CLI.managedsoftwareupdate;
using Xunit;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// What --show-config prints for AdditionalHttpHeaders: the names, never the values.
/// </summary>
public class ShowConfigHeadersTests
{
    [Fact]
    public void DescribeAdditionalHttpHeaders_NoEntries_ReportsNotSet()
    {
        Assert.Equal("(not set)", Program.DescribeAdditionalHttpHeaders(null));
        Assert.Equal("(not set)", Program.DescribeAdditionalHttpHeaders([]));
    }

    [Fact]
    public void DescribeAdditionalHttpHeaders_Entries_ListsNamesAndMasksValues()
    {
        var text = Program.DescribeAdditionalHttpHeaders(
            ["X-Serial-Number: ABC123", "X-Site: north: with colon"]);

        Assert.Equal("X-Serial-Number: ***, X-Site: ***", text);
        Assert.DoesNotContain("ABC123", text);
        Assert.DoesNotContain("north", text);
    }

    [Fact]
    public void DescribeAdditionalHttpHeaders_MalformedEntry_ShowsPositionNotText()
    {
        var text = Program.DescribeAdditionalHttpHeaders(["X-Good: 1", "no colon here", ": value only"]);

        Assert.Equal("X-Good: ***, (entry 2: not \"Name: value\"), (entry 3: not \"Name: value\")", text);
        Assert.DoesNotContain("no colon here", text);
    }
}
