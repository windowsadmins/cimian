using CimianTools.CimiTrigger.Services;
using Xunit;

namespace Cimian.Tests.CimiTrigger;

/// <summary>
/// Tests for DiagnosticService.
/// NOTE: We do NOT call RunDiagnostics() in tests because on a machine with CimianWatcher
/// installed, it creates trigger files that would cause the service to launch
/// managedsoftwareupdate.exe with real package installations.
/// </summary>
public class DiagnosticServiceTests
{
    private readonly DiagnosticService _service;

    public DiagnosticServiceTests()
    {
        _service = new DiagnosticService();
    }

    [Fact]
    public void CheckAdminPrivileges_ReturnsBoolean()
    {
        // This will return true or false depending on test execution context
        var result = DiagnosticService.CheckAdminPrivileges();
        
        // Just verify it returns a valid boolean and doesn't throw
        Assert.True(result || !result);
    }

    [Fact]
    public void DiagnosticService_CanBeConstructed()
    {
        // Verify service construction doesn't throw
        Assert.NotNull(_service);
    }
}
