using Xunit;
using Cimian.CLI.managedsoftwareupdate.Models;
using Cimian.CLI.managedsoftwareupdate.Services;

namespace Cimian.Tests.Managedsoftwareupdate;

/// <summary>
/// Tests for StatusService - installation status checking and system information.
/// </summary>
public class StatusServiceTests
{
    private readonly StatusService _service;
    private readonly string _testDir;

    public StatusServiceTests()
    {
        _service = new StatusService();
        _testDir = Path.Combine(Path.GetTempPath(), "CimianTests", "Status", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    #region CheckStatus Tests

    [Fact]
    public void CheckStatus_NewItem_WithNoChecks_AssumesInstalled()
    {
        // Go parity: Items without any verification methods are assumed installed
        // because there's no way to verify their installation status
        var item = new CatalogItem
        {
            Name = "NonExistentPackage12345",
            Version = "1.0.0"
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        // No checks defined = assume installed (Go parity)
        Assert.False(result.NeedsAction);
        Assert.Equal("installed", result.Status);
    }

    [Fact]
    public void CheckStatus_NoChecks_UsesDefaultCheck()
    {
        var item = new CatalogItem
        {
            Name = "NoCheckPackage",
            Version = "1.0.0",
            Check = new CheckInfo() // Empty check
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        // Should use default ManagedInstalls registry check
        Assert.NotNull(result);
        Assert.NotEmpty(result.Status);
    }

    #endregion

    #region Static Method Tests

    [Fact]
    public void IsAdministrator_ReturnsBoolean()
    {
        // This will return true or false depending on how tests are run
        var result = StatusService.IsAdministrator();

        Assert.IsType<bool>(result);
    }

    [Fact]
    public void GetSystemArchitecture_ReturnsValidValue()
    {
        var arch = StatusService.GetSystemArchitecture();

        Assert.NotNull(arch);
        Assert.NotEmpty(arch);
        Assert.True(arch == "x64" || arch == "x86" || arch == "arm64" || arch == "arm");
    }

    [Fact]
    public void GetIdleSeconds_ReturnsNonNegative()
    {
        var idleSeconds = StatusService.GetIdleSeconds();

        Assert.True(idleSeconds >= 0);
    }

    [Fact]
    public void IsUserActive_ReturnsBoolean()
    {
        var result = StatusService.IsUserActive();

        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsBootstrapMode_ReturnsBoolean()
    {
        var result = StatusService.IsBootstrapMode();

        Assert.IsType<bool>(result);
    }

    #endregion

    #region StatusCheckResult Tests

    [Fact]
    public void StatusCheckResult_Status_HasValue()
    {
        var item = new CatalogItem
        {
            Name = "TestPackage",
            Version = "1.0.0"
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.NotNull(result.Status);
        Assert.True(
            result.Status == "installed" ||
            result.Status == "pending" ||
            result.Status == "error" ||
            result.Status == "unknown"
        );
    }

    [Fact]
    public void StatusCheckResult_Reason_HasValue()
    {
        var item = new CatalogItem
        {
            Name = "TestPackageForReason",
            Version = "1.0.0"
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.NotNull(result.Reason);
    }

    #endregion

    #region Registry Check Tests

    [Fact]
    public void CheckStatus_RegistryCheck_HandlesNonExistentApp()
    {
        var item = new CatalogItem
        {
            Name = "NonExistentApp99999",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                Registry = new RegistryCheck
                {
                    Name = "NonExistentApp99999"
                }
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("pending", result.Status);
        Assert.True(result.NeedsAction);
    }

    #endregion

    #region File Check Tests

    [Fact]
    public void CheckStatus_FileCheck_FileNotFound_NeedsAction()
    {
        var item = new CatalogItem
        {
            Name = "FileCheckPackage",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                File = new FileCheck
                {
                    Path = @"C:\NonExistent\Path\file.exe"
                }
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("pending", result.Status);
        Assert.True(result.NeedsAction);
    }

    [Fact]
    public void CheckStatus_FileCheck_FileExists_Installed()
    {
        var testFile = Path.Combine(_testDir, "exists.exe");
        File.WriteAllText(testFile, "test content");

        var item = new CatalogItem
        {
            Name = "ExistingFilePackage",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                File = new FileCheck
                {
                    Path = testFile
                }
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("installed", result.Status);
        Assert.False(result.NeedsAction);
    }

    [Fact]
    public void CheckStatus_FileCheck_HashMismatch_NeedsAction()
    {
        var testFile = Path.Combine(_testDir, "hashtest.exe");
        File.WriteAllText(testFile, "test content");

        var item = new CatalogItem
        {
            Name = "HashMismatchPackage",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                File = new FileCheck
                {
                    Path = testFile,
                    Hash = "0000000000000000000000000000000000000000000000000000000000000000" // Wrong hash
                }
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("pending", result.Status);
        Assert.True(result.NeedsAction);
        Assert.Contains("mismatch", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Script Check Tests

    [Fact]
    public void CheckStatus_ScriptCheck_SuccessScript_ReturnsInstalled()
    {
        var item = new CatalogItem
        {
            Name = "ScriptSuccessPackage",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                Script = "exit 0"
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("installed", result.Status);
        Assert.False(result.NeedsAction);
    }

    [Fact]
    public void CheckStatus_ScriptCheck_FailScript_NeedsAction()
    {
        var item = new CatalogItem
        {
            Name = "ScriptFailPackage",
            Version = "1.0.0",
            Check = new CheckInfo
            {
                Script = "Write-Error 'Not installed'; exit 1"
            }
        };

        var result = _service.CheckStatus(item, "install", _testDir);

        Assert.Equal("pending", result.Status);
        Assert.True(result.NeedsAction);
    }

    #endregion
}
