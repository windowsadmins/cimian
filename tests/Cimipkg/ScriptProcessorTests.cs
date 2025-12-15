using System.Collections.Generic;
using Cimian.CLI.Cimipkg.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cimian.Tests.Cimipkg;

/// <summary>
/// Tests for the ScriptProcessor service.
/// </summary>
public class ScriptProcessorTests
{
    private readonly ScriptProcessor _processor;

    public ScriptProcessorTests()
    {
        var logger = new Mock<ILogger<ScriptProcessor>>();
        _processor = new ScriptProcessor(logger.Object);
    }

    #region Placeholder Replacement Tests

    [Fact]
    public void ReplacePlaceholders_CurlyBraceSyntax_ReplacesCorrectly()
    {
        var content = "Hello ${NAME}, welcome to ${PLACE}!";
        var envVars = new Dictionary<string, string>
        {
            ["NAME"] = "World",
            ["PLACE"] = "Cimian"
        };

        var result = _processor.ReplacePlaceholders(content, envVars);

        Assert.Equal("Hello World, welcome to Cimian!", result);
    }

    [Fact]
    public void ReplacePlaceholders_DollarSignSyntax_ReplacesCorrectly()
    {
        var content = "Value: $MY_VAR and $OTHER_VAR";
        var envVars = new Dictionary<string, string>
        {
            ["MY_VAR"] = "123",
            ["OTHER_VAR"] = "456"
        };

        var result = _processor.ReplacePlaceholders(content, envVars);

        Assert.Equal("Value: 123 and 456", result);
    }

    [Fact]
    public void ReplacePlaceholders_MixedSyntax_ReplacesAll()
    {
        var content = "${VAR1} and $VAR2";
        var envVars = new Dictionary<string, string>
        {
            ["VAR1"] = "First",
            ["VAR2"] = "Second"
        };

        var result = _processor.ReplacePlaceholders(content, envVars);

        Assert.Equal("First and Second", result);
    }

    [Fact]
    public void ReplacePlaceholders_UnknownVariable_PreservesPlaceholder()
    {
        var content = "Known: ${KNOWN}, Unknown: ${UNKNOWN}";
        var envVars = new Dictionary<string, string>
        {
            ["KNOWN"] = "value"
        };

        var result = _processor.ReplacePlaceholders(content, envVars);

        Assert.Equal("Known: value, Unknown: ${UNKNOWN}", result);
    }

    [Fact]
    public void ReplacePlaceholders_EmptyEnvVars_ReturnsOriginal()
    {
        var content = "No replacements: ${VAR}";
        var envVars = new Dictionary<string, string>();

        var result = _processor.ReplacePlaceholders(content, envVars);

        Assert.Equal("No replacements: ${VAR}", result);
    }

    [Fact]
    public void ReplacePlaceholders_NullContent_ReturnsNull()
    {
        var envVars = new Dictionary<string, string> { ["VAR"] = "value" };

        var result = _processor.ReplacePlaceholders(null!, envVars);

        Assert.Null(result);
    }

    [Fact]
    public void ReplacePlaceholders_NullEnvVars_ReturnsOriginal()
    {
        var content = "Hello ${NAME}";

        var result = _processor.ReplacePlaceholders(content, null!);

        Assert.Equal("Hello ${NAME}", result);
    }

    #endregion

    #region YAML Placeholder Replacement Tests

    [Fact]
    public void ReplacePlaceholdersYaml_OnlyCurlyBraceSyntax_ReplacesCorrectly()
    {
        var content = "value: ${MY_VALUE}";
        var envVars = new Dictionary<string, string>
        {
            ["MY_VALUE"] = "test"
        };

        var result = _processor.ReplacePlaceholdersYaml(content, envVars);

        Assert.Equal("value: test", result);
    }

    [Fact]
    public void ReplacePlaceholdersYaml_DollarSignSyntax_NotReplaced()
    {
        // In YAML, $var could be valid YAML syntax, so we only replace ${var}
        var content = "value: $MY_VALUE";
        var envVars = new Dictionary<string, string>
        {
            ["MY_VALUE"] = "test"
        };

        var result = _processor.ReplacePlaceholdersYaml(content, envVars);

        // Should NOT be replaced - only ${} syntax in YAML
        Assert.Equal("value: $MY_VALUE", result);
    }

    #endregion

    #region Script Processing Tests

    [Fact]
    public void ProcessScript_PowerShellWithHeader_InjectsHeader()
    {
        var content = "Write-Host 'Hello'";
        var envVars = new Dictionary<string, string>();

        var result = _processor.ProcessScript(content, ".ps1", envVars, injectHeader: true);

        Assert.Contains("# cimipkg: Auto-mapped variables", result);
        Assert.Contains("$payloadRoot", result);
        Assert.Contains("$payloadDir", result);
        Assert.Contains("$installLocation", result);
        Assert.Contains("Write-Host 'Hello'", result);
    }

    [Fact]
    public void ProcessScript_PowerShellNoHeader_NoHeaderInjected()
    {
        var content = "Write-Host 'Hello'";
        var envVars = new Dictionary<string, string>();

        var result = _processor.ProcessScript(content, ".ps1", envVars, injectHeader: false);

        Assert.DoesNotContain("# cimipkg: Auto-mapped variables", result);
        Assert.Equal("Write-Host 'Hello'", result);
    }

    [Fact]
    public void ProcessScript_BatchFile_NoHeaderInjected()
    {
        var content = "echo Hello";
        var envVars = new Dictionary<string, string>();

        var result = _processor.ProcessScript(content, ".bat", envVars, injectHeader: true);

        Assert.DoesNotContain("# cimipkg: Auto-mapped variables", result);
        Assert.Equal("echo Hello", result);
    }

    [Fact]
    public void ProcessScript_ReplacesAndInjectsHeader()
    {
        var content = "Write-Host '${MESSAGE}'";
        var envVars = new Dictionary<string, string>
        {
            ["MESSAGE"] = "Hello World"
        };

        var result = _processor.ProcessScript(content, ".ps1", envVars, injectHeader: true);

        Assert.Contains("# cimipkg: Auto-mapped variables", result);
        Assert.Contains("Write-Host 'Hello World'", result);
    }

    #endregion

    #region Script Type Detection Tests

    [Theory]
    [InlineData(".ps1", true)]
    [InlineData(".PS1", true)]
    [InlineData(".psm1", true)]
    [InlineData(".psd1", true)]
    [InlineData(".bat", false)]
    [InlineData(".cmd", false)]
    [InlineData(".sh", false)]
    public void IsPowerShellScript_ReturnsExpected(string extension, bool expected)
    {
        Assert.Equal(expected, ScriptProcessor.IsPowerShellScript(extension));
    }

    [Theory]
    [InlineData(".ps1", true)]
    [InlineData(".psm1", true)]
    [InlineData(".psd1", true)]
    [InlineData(".bat", true)]
    [InlineData(".cmd", true)]
    [InlineData(".sh", true)]
    [InlineData(".txt", false)]
    [InlineData(".xml", false)]
    [InlineData(".dll", false)]
    public void IsScriptFile_ReturnsExpected(string extension, bool expected)
    {
        Assert.Equal(expected, ScriptProcessor.IsScriptFile(extension));
    }

    #endregion

    #region Script Combination Tests

    [Fact]
    public void CombineScripts_MultipleScripts_CombinesWithHeaders()
    {
        // Create temp script files
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var script1 = Path.Combine(tempDir, "preinstall01.ps1");
            var script2 = Path.Combine(tempDir, "preinstall02.ps1");

            File.WriteAllText(script1, "# Script 1\nWrite-Host 'First'");
            File.WriteAllText(script2, "# Script 2\nWrite-Host 'Second'");

            var envVars = new Dictionary<string, string>();
            var result = _processor.CombineScripts([script1, script2], envVars);

            Assert.Contains("Included from: preinstall01.ps1", result);
            Assert.Contains("Included from: preinstall02.ps1", result);
            Assert.Contains("Write-Host 'First'", result);
            Assert.Contains("Write-Host 'Second'", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CombineScripts_WithPlaceholders_ReplacesInAll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var script1 = Path.Combine(tempDir, "script1.ps1");
            File.WriteAllText(script1, "Write-Host '${MSG}'");

            var envVars = new Dictionary<string, string> { ["MSG"] = "Hello" };
            var result = _processor.CombineScripts([script1], envVars);

            Assert.Contains("Write-Host 'Hello'", result);
            Assert.DoesNotContain("${MSG}", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region Script File Discovery Tests

    [Fact]
    public void GetPreinstallScripts_ReturnsOrderedList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        var scriptsDir = Path.Combine(tempDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        try
        {
            File.WriteAllText(Path.Combine(scriptsDir, "preinstall02.ps1"), "# 2");
            File.WriteAllText(Path.Combine(scriptsDir, "preinstall01.ps1"), "# 1");
            File.WriteAllText(Path.Combine(scriptsDir, "preinstall03.ps1"), "# 3");
            File.WriteAllText(Path.Combine(scriptsDir, "other.ps1"), "# other");

            var result = _processor.GetPreinstallScripts(scriptsDir);

            Assert.Equal(3, result.Count);
            Assert.Contains("preinstall01.ps1", result[0]);
            Assert.Contains("preinstall02.ps1", result[1]);
            Assert.Contains("preinstall03.ps1", result[2]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetPostinstallScripts_ReturnsOrderedList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        var scriptsDir = Path.Combine(tempDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        try
        {
            File.WriteAllText(Path.Combine(scriptsDir, "postinstall02.ps1"), "# 2");
            File.WriteAllText(Path.Combine(scriptsDir, "postinstall01.ps1"), "# 1");

            var result = _processor.GetPostinstallScripts(scriptsDir);

            Assert.Equal(2, result.Count);
            Assert.Contains("postinstall01.ps1", result[0]);
            Assert.Contains("postinstall02.ps1", result[1]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetUninstallScript_WhenExists_ReturnsPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        var scriptsDir = Path.Combine(tempDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        try
        {
            var uninstallPath = Path.Combine(scriptsDir, "uninstall.ps1");
            File.WriteAllText(uninstallPath, "# uninstall");

            var result = _processor.GetUninstallScript(scriptsDir);

            Assert.NotNull(result);
            Assert.Contains("uninstall.ps1", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetUninstallScript_WhenNotExists_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimipkg_test_{Guid.NewGuid():N}");
        var scriptsDir = Path.Combine(tempDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        try
        {
            var result = _processor.GetUninstallScript(scriptsDir);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetPreinstallScripts_NonexistentDir_ReturnsEmpty()
    {
        var result = _processor.GetPreinstallScripts("/nonexistent/path");

        Assert.Empty(result);
    }

    #endregion
}
