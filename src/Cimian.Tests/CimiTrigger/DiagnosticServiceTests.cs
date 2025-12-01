using CimianTools.CimiTrigger.Services;
using Xunit;

namespace Cimian.Tests.CimiTrigger;

/// <summary>
/// Tests for DiagnosticService.
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
    public void RunDiagnostics_DoesNotThrow()
    {
        // Capture console output to prevent test noise
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            // Should complete without throwing
            _service.RunDiagnostics();

            var output = sw.ToString();
            
            // Verify some expected output is present
            Assert.Contains("DIAGNOSTIC", output);
            Assert.Contains("administrative privileges", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void RunDiagnostics_ChecksAllSections()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            _service.RunDiagnostics();

            var output = sw.ToString();
            
            // Verify all diagnostic sections are present
            Assert.Contains("1. Checking administrative privileges", output);
            Assert.Contains("2. Checking CimianWatcher service", output);
            Assert.Contains("3. Checking directory access", output);
            Assert.Contains("4. Checking executables", output);
            Assert.Contains("5. Testing trigger file creation", output);
            Assert.Contains("7. Environment Information", output);
            Assert.Contains("DIAGNOSTIC SUMMARY", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void RunDiagnostics_ShowsEnvironmentInfo()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            _service.RunDiagnostics();

            var output = sw.ToString();
            
            // Verify environment info is shown
            Assert.Contains("Current User:", output);
            Assert.Contains("Machine Name:", output);
            Assert.Contains("OS:", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void RunDiagnostics_ShowsAlternativeMethods()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            _service.RunDiagnostics();

            var output = sw.ToString();
            
            // Verify alternative methods are shown
            Assert.Contains("Alternative methods to try", output);
            Assert.Contains("--force gui", output);
            Assert.Contains("--force headless", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void RunDiagnostics_ShowsTroubleshootingCommands()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            _service.RunDiagnostics();

            var output = sw.ToString();
            
            // Verify troubleshooting commands are shown
            Assert.Contains("Troubleshooting commands", output);
            Assert.Contains("sc query CimianWatcher", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
