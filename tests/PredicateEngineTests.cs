using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Cimian.Core.Models;
using Cimian.Engine.Predicates;
using Xunit;

namespace Cimian.Tests;

/// <summary>
/// Comprehensive test suite for PredicateEngine
/// Migrated from Go pkg/predicates/predicates.go and pkg/manifest/manifest.go
/// Tests NSPredicate-style conditional evaluation with complex expressions
/// </summary>
public class PredicateEngineTests
{
    private readonly PredicateEngine _engine;
    private readonly Mock<ILogger<PredicateEngine>> _loggerMock;

    public PredicateEngineTests()
    {
        _loggerMock = new Mock<ILogger<PredicateEngine>>();
        _engine = new PredicateEngine(_loggerMock.Object);
    }

    #region Simple Comparison Tests

    [Theory]
    [InlineData("hostname == 'DESIGN-001'", "DESIGN-001", true)]
    [InlineData("hostname == 'DESIGN-001'", "design-001", true)] // Case insensitive
    [InlineData("hostname == 'DESIGN-001'", "STUDIO-001", false)]
    [InlineData("hostname == \"DESIGN-001\"", "DESIGN-001", true)] // Double quotes
    public async Task EvaluateCondition_EqualsOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname != 'DESIGN-001'", "DESIGN-001", false)]
    [InlineData("hostname != 'DESIGN-001'", "STUDIO-001", true)]
    public async Task EvaluateCondition_NotEqualsOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname CONTAINS 'Design'", "DESIGN-001", true)]
    [InlineData("hostname CONTAINS 'Design'", "design-workstation", true)]
    [InlineData("hostname CONTAINS 'Design'", "STUDIO-001", false)]
    [InlineData("hostname CONTAINS 'Studio'", "STUDIO-RENDER-01", true)]
    public async Task EvaluateCondition_ContainsOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname BEGINSWITH 'DESIGN'", "DESIGN-001", true)]
    [InlineData("hostname BEGINSWITH 'DESIGN'", "MY-DESIGN-001", false)]
    [InlineData("hostname BEGINSWITH 'Studio'", "Studio-Render-01", true)]
    public async Task EvaluateCondition_BeginsWithOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname ENDSWITH '001'", "DESIGN-001", true)]
    [InlineData("hostname ENDSWITH '001'", "DESIGN-002", false)]
    [InlineData("hostname ENDSWITH 'Workstation'", "Design-Workstation", true)]
    public async Task EvaluateCondition_EndsWithOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname DOES_NOT_CONTAIN 'Camera'", "DESIGN-001", true)]
    [InlineData("hostname DOES_NOT_CONTAIN 'Camera'", "ANIM-Camera-01", false)]
    [InlineData("hostname DOES_NOT_CONTAIN 'Design'", "DESIGN-001", false)]
    [InlineData("hostname DOES_NOT_CONTAIN 'Kiosk'", "LOBBY-DISPLAY", true)]
    public async Task EvaluateCondition_DoesNotContainOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateCondition_DoesNotContainWithAnd_ShouldMatch()
    {
        // This is the real-world condition from Cintiq manifest
        var condition = "hostname DOES_NOT_CONTAIN 'Camera' AND hostname DOES_NOT_CONTAIN 'ANIM-CAM'";
        
        var facts1 = CreateFacts(hostname: "Cintiq22-01");
        var result1 = await _engine.EvaluateConditionAsync(condition, facts1);
        result1.Should().BeTrue("Cintiq22-01 should match (no Camera or ANIM-CAM)");
        
        var facts2 = CreateFacts(hostname: "ANIM-Camera-01");
        var result2 = await _engine.EvaluateConditionAsync(condition, facts2);
        result2.Should().BeFalse("ANIM-Camera-01 should NOT match (contains Camera)");
        
        var facts3 = CreateFacts(hostname: "ANIM-CAM-05");
        var result3 = await _engine.EvaluateConditionAsync(condition, facts3);
        result3.Should().BeFalse("ANIM-CAM-05 should NOT match (contains ANIM-CAM)");
    }

    [Fact]
    public async Task EvaluateCondition_ComplexOrWithContains_ShouldMatch()
    {
        // This is the real-world condition from Cintiq manifest for lab matching
        var condition = "hostname CONTAINS 'Cintiq-1' OR hostname CONTAINS 'Cintiq-0'";
        
        var facts1 = CreateFacts(hostname: "Cintiq-15");
        var result1 = await _engine.EvaluateConditionAsync(condition, facts1);
        result1.Should().BeTrue("Cintiq-15 should match (contains Cintiq-1)");
        
        var facts2 = CreateFacts(hostname: "Cintiq-02");
        var result2 = await _engine.EvaluateConditionAsync(condition, facts2);
        result2.Should().BeTrue("Cintiq-02 should match (contains Cintiq-0)");
        
        var facts3 = CreateFacts(hostname: "Cintiq-22");
        var result3 = await _engine.EvaluateConditionAsync(condition, facts3);
        result3.Should().BeFalse("Cintiq-22 should NOT match (no Cintiq-1 or Cintiq-0)");
    }

    [Theory]
    [InlineData("arch == 'x64'", "x64", true)]
    [InlineData("arch == 'x64'", "ARM64", false)]
    [InlineData("architecture == 'ARM64'", "ARM64", true)]
    public async Task EvaluateCondition_ArchitectureFact_ShouldMatch(string condition, string arch, bool expected)
    {
        var facts = CreateFacts(arch: arch);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    #endregion

    #region Numeric Comparison Tests

    [Theory]
    [InlineData("os_vers_major > 10", 11, true)]
    [InlineData("os_vers_major > 10", 10, false)]
    [InlineData("os_vers_major > 10", 9, false)]
    [InlineData("os_vers_major >= 10", 10, true)]
    [InlineData("os_vers_major >= 10", 11, true)]
    [InlineData("os_vers_major >= 10", 9, false)]
    public async Task EvaluateCondition_GreaterThanOperator_ShouldMatch(string condition, int osVersion, bool expected)
    {
        var facts = CreateFacts(osVersMajor: osVersion);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("os_vers_major < 11", 10, true)]
    [InlineData("os_vers_major < 11", 11, false)]
    [InlineData("os_vers_major < 11", 12, false)]
    [InlineData("os_vers_major <= 11", 11, true)]
    [InlineData("os_vers_major <= 11", 10, true)]
    [InlineData("os_vers_major <= 11", 12, false)]
    public async Task EvaluateCondition_LessThanOperator_ShouldMatch(string condition, int osVersion, bool expected)
    {
        var facts = CreateFacts(osVersMajor: osVersion);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    #endregion

    #region OR Expression Tests

    [Theory]
    [InlineData("hostname CONTAINS 'Design' OR hostname CONTAINS 'Studio'", "DESIGN-001", true)]
    [InlineData("hostname CONTAINS 'Design' OR hostname CONTAINS 'Studio'", "STUDIO-001", true)]
    [InlineData("hostname CONTAINS 'Design' OR hostname CONTAINS 'Studio'", "KIOSK-001", false)]
    public async Task EvaluateCondition_OrOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hostname CONTAINS 'Design' or hostname CONTAINS 'Studio'", "DESIGN-001", true)]
    [InlineData("hostname CONTAINS 'Design' or hostname CONTAINS 'Studio'", "STUDIO-001", true)]
    public async Task EvaluateCondition_OrOperatorLowercase_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateCondition_MultipleOrExpressions_ShouldMatch()
    {
        var facts = CreateFacts(hostname: "RENDER-FARM-01");
        
        // Should match the third OR condition
        var result = await _engine.EvaluateConditionAsync(
            "hostname CONTAINS 'Design' OR hostname CONTAINS 'Studio' OR hostname CONTAINS 'Render'", 
            facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region AND Expression Tests

    [Theory]
    [InlineData("hostname CONTAINS 'Design' AND arch == 'x64'", "DESIGN-001", "x64", true)]
    [InlineData("hostname CONTAINS 'Design' AND arch == 'x64'", "DESIGN-001", "ARM64", false)]
    [InlineData("hostname CONTAINS 'Design' AND arch == 'x64'", "STUDIO-001", "x64", false)]
    public async Task EvaluateCondition_AndOperator_ShouldMatch(string condition, string hostname, string arch, bool expected)
    {
        var facts = CreateFacts(hostname: hostname, arch: arch);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateCondition_MultipleAndExpressions_ShouldMatch()
    {
        var facts = CreateFacts(
            hostname: "DESIGN-WIN11-X64", 
            arch: "x64", 
            osVersMajor: 11,
            domain: "CORP");
        
        var result = await _engine.EvaluateConditionAsync(
            "hostname CONTAINS 'DESIGN' AND arch == 'x64' AND os_vers_major >= 11", 
            facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region NOT Expression Tests

    [Theory]
    [InlineData("NOT hostname CONTAINS 'Kiosk'", "DESIGN-001", true)]
    [InlineData("NOT hostname CONTAINS 'Kiosk'", "KIOSK-001", false)]
    [InlineData("NOT hostname CONTAINS 'Kiosk'", "LOBBY-KIOSK", false)]
    public async Task EvaluateCondition_NotOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateCondition_NotWithAnd_ShouldMatch()
    {
        var facts = CreateFacts(hostname: "DESIGN-001", arch: "x64");
        
        var result = await _engine.EvaluateConditionAsync(
            "NOT hostname CONTAINS 'Kiosk' AND arch == 'x64'", 
            facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region Parentheses Tests

    [Theory]
    [InlineData("(hostname CONTAINS 'Design')", "DESIGN-001", true)]
    [InlineData("(hostname CONTAINS 'Design')", "STUDIO-001", false)]
    public async Task EvaluateCondition_SimpleParentheses_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateCondition_NestedParenthesesWithOr_ShouldMatch()
    {
        // NOT (kiosk machines) AND (corp domain OR edu domain)
        var facts = CreateFacts(hostname: "DESIGN-001", domain: "CORP.LOCAL");
        
        var result = await _engine.EvaluateConditionAsync(
            "NOT hostname CONTAINS 'Kiosk' AND (domain CONTAINS 'CORP' OR domain CONTAINS 'EDU')", 
            facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_ComplexParentheses_ShouldMatch()
    {
        var facts = CreateFacts(hostname: "STUDIO-RENDER-01", domain: "PRODUCTION", arch: "x64");
        
        // Match: (Studio OR Design machines) AND (Production domain) AND (x64 arch)
        var result = await _engine.EvaluateConditionAsync(
            "(hostname CONTAINS 'Studio' OR hostname CONTAINS 'Design') AND domain == 'PRODUCTION' AND arch == 'x64'", 
            facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region ANY Expression Tests

    [Fact]
    public async Task EvaluateCondition_AnyCatalogsContains_ShouldMatch()
    {
        var facts = CreateFacts(hostname: "DESIGN-001", catalogs: new[] { "Production", "Design", "Testing" });
        
        var result = await _engine.EvaluateConditionAsync(
            "ANY catalogs == 'Design'", 
            facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_AnyCatalogsNotEquals_ShouldMatchWhenSomeNotMatch()
    {
        var facts = CreateFacts(hostname: "DESIGN-001", catalogs: new[] { "Production", "Design" });
        
        // ANY catalogs != 'Testing' - at least one catalog is not 'Testing'
        var result = await _engine.EvaluateConditionAsync(
            "ANY catalogs != 'Testing'", 
            facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_AnyCatalogsNotFound_ShouldNotMatch()
    {
        var facts = CreateFacts(hostname: "DESIGN-001", catalogs: new[] { "Production", "QA" });
        
        var result = await _engine.EvaluateConditionAsync(
            "ANY catalogs == 'Design'", 
            facts);
        
        result.Should().BeFalse();
    }

    #endregion

    #region Domain and Machine Type Tests

    [Theory]
    [InlineData("domain == 'CORP.LOCAL'", "CORP.LOCAL", true)]
    [InlineData("domain == 'CORP.LOCAL'", "WORKGROUP", false)]
    [InlineData("domain CONTAINS 'CORP'", "CORP.LOCAL", true)]
    public async Task EvaluateCondition_DomainFact_ShouldMatch(string condition, string domain, bool expected)
    {
        var facts = CreateFacts(domain: domain);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("machine_type == 'laptop'", "laptop", true)]
    [InlineData("machine_type == 'laptop'", "desktop", false)]
    [InlineData("machine_type == 'desktop'", "desktop", true)]
    public async Task EvaluateCondition_MachineTypeFact_ShouldMatch(string condition, string machineType, bool expected)
    {
        var facts = CreateFacts(machineType: machineType);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("joined_type == 'domain'", "domain", true)]
    [InlineData("joined_type == 'domain'", "workgroup", false)]
    [InlineData("joined_type == 'entra'", "entra", true)]
    [InlineData("joined_type == 'hybrid'", "hybrid", true)]
    public async Task EvaluateCondition_JoinedTypeFact_ShouldMatch(string condition, string joinedType, bool expected)
    {
        var facts = CreateFacts(joinedType: joinedType);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    #endregion

    #region Empty and Null Condition Tests

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task EvaluateCondition_EmptyOrNullCondition_ShouldReturnTrue(string? condition)
    {
        var facts = CreateFacts();
        var result = await _engine.EvaluateConditionAsync(condition!, facts);
        result.Should().BeTrue();
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public async Task EvaluateCondition_DesignDepartmentScenario_ShouldMatch()
    {
        // Scenario: Deploy design software to Design and Studio machines on x64 Windows 11+
        var facts = CreateFacts(
            hostname: "DESIGN-WS-001",
            arch: "x64",
            osVersMajor: 11,
            domain: "CORP.LOCAL",
            machineType: "desktop");
        
        var condition = "(hostname CONTAINS 'Design' OR hostname CONTAINS 'Studio') AND arch == 'x64' AND os_vers_major >= 11";
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_KioskExclusionScenario_ShouldMatch()
    {
        // Scenario: Deploy to all domain machines except kiosks
        var facts = CreateFacts(
            hostname: "RECEPTION-PC-001",
            domain: "CORP.LOCAL",
            joinedType: "domain");
        
        var condition = "NOT hostname CONTAINS 'Kiosk' AND joined_type == 'domain'";
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_KioskExclusionScenario_ShouldNotMatchKiosk()
    {
        var facts = CreateFacts(
            hostname: "LOBBY-KIOSK-001",
            domain: "CORP.LOCAL",
            joinedType: "domain");
        
        var condition = "NOT hostname CONTAINS 'Kiosk' AND joined_type == 'domain'";
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateCondition_LaptopWithBatteryScenario_ShouldMatch()
    {
        var facts = CreateFacts(
            hostname: "MOBILE-USER-001",
            machineType: "laptop",
            domain: "CORP.LOCAL");
        
        var condition = "machine_type == 'laptop' AND domain CONTAINS 'CORP'";
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_HybridEntraJoinedScenario_ShouldMatch()
    {
        // Scenario: Deploy cloud-connected features to hybrid or Entra joined devices
        var facts = CreateFacts(
            hostname: "CLOUD-PC-001",
            joinedType: "hybrid");
        
        var condition = "joined_type == 'hybrid' OR joined_type == 'entra'";
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region Parser Edge Cases

    [Fact]
    public async Task EvaluateCondition_ValueWithSpaces_ShouldMatch()
    {
        var facts = CreateFacts(machineModel: "Dell OptiPlex 7090");
        
        var result = await _engine.EvaluateConditionAsync(
            "machine_model CONTAINS 'OptiPlex'", 
            facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateCondition_QuotedValueWithSpaces_ShouldMatch()
    {
        var facts = CreateFacts(machineModel: "Dell OptiPlex 7090");
        
        var result = await _engine.EvaluateConditionAsync(
            "machine_model == 'Dell OptiPlex 7090'", 
            facts);
        
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("hostname LIKE '*Design*'", "DESIGN-001", true)]
    [InlineData("hostname LIKE '*Design*'", "STUDIO-001", false)]
    public async Task EvaluateCondition_LikeOperator_ShouldMatch(string condition, string hostname, bool expected)
    {
        var facts = CreateFacts(hostname: hostname);
        var result = await _engine.EvaluateConditionAsync(condition, facts);
        result.Should().Be(expected);
    }

    #endregion

    #region ConditionalItem Tests

    [Fact]
    public async Task EvaluateAsync_ConditionalItemWithNoCondition_ShouldReturnTrue()
    {
        var item = new ConditionalItem { Name = "Always", Condition = null };
        var facts = CreateFacts();
        
        var result = await _engine.EvaluateAsync(item, facts);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ConditionalItemWithMatchingCondition_ShouldReturnTrue()
    {
        var item = new ConditionalItem 
        { 
            Name = "DesignSoftware", 
            Condition = "hostname CONTAINS 'Design'" 
        };
        var facts = CreateFacts(hostname: "DESIGN-001");
        
        var result = await _engine.EvaluateAsync(item, facts);
        
        result.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private static SystemFacts CreateFacts(
        string hostname = "TEST-PC",
        string arch = "x64",
        int osVersMajor = 10,
        string domain = "WORKGROUP",
        string machineType = "desktop",
        string joinedType = "workgroup",
        string machineModel = "Generic PC",
        string[]? catalogs = null)
    {
        return new SystemFacts
        {
            Hostname = hostname,
            Architecture = arch,
            OSVersMajor = osVersMajor,
            Domain = domain,
            MachineType = machineType,
            JoinedType = joinedType,
            MachineModel = machineModel,
            Catalogs = catalogs?.ToList() ?? new List<string>()
        };
    }

    #endregion
}
