using CimianTools.CimiTrigger.Services;
using Xunit;

namespace Cimian.Tests.CimiTrigger;

/// <summary>
/// Tests for TriggerService.
/// </summary>
public class TriggerServiceTests : IDisposable
{
    private readonly TriggerService _service;
    private readonly string _testDir;
    private readonly List<string> _createdFiles = [];

    public TriggerServiceTests()
    {
        _service = new TriggerService();
        _testDir = Path.Combine(Path.GetTempPath(), "cimitrigger_tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        // Clean up test files
        foreach (var file in _createdFiles)
        {
            try { File.Delete(file); } catch { }
        }

        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void GuiBootstrapFile_HasCorrectPath()
    {
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\.cimian.bootstrap", TriggerService.GuiBootstrapFile);
    }

    [Fact]
    public void HeadlessBootstrapFile_HasCorrectPath()
    {
        Assert.Equal(@"C:\ProgramData\ManagedInstalls\.cimian.headless", TriggerService.HeadlessBootstrapFile);
    }

    [Fact]
    public void CreateTriggerFile_CreatesFileWithContent()
    {
        var testFile = Path.Combine(_testDir, ".test.trigger");
        _createdFiles.Add(testFile);

        var result = _service.CreateTriggerFile(testFile, "GUI");

        Assert.True(result);
        Assert.True(File.Exists(testFile));

        var content = File.ReadAllText(testFile);
        Assert.Contains("Bootstrap triggered at:", content);
        Assert.Contains("Mode: GUI", content);
        Assert.Contains("Triggered by: cimitrigger CLI", content);
    }

    [Fact]
    public void CreateTriggerFile_CreatesDirectoryIfNeeded()
    {
        var nestedDir = Path.Combine(_testDir, "nested", "deep");
        var testFile = Path.Combine(nestedDir, ".test.trigger");
        _createdFiles.Add(testFile);

        var result = _service.CreateTriggerFile(testFile, "headless");

        Assert.True(result);
        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(testFile));
        
        var content = File.ReadAllText(testFile);
        Assert.Contains("Mode: headless", content);
    }

    [Fact]
    public void CreateTriggerFile_ReturnsFalse_OnInvalidPath()
    {
        // Try to write to a path that will fail
        var result = _service.CreateTriggerFile("Z:\\NonExistentDrive\\file.txt", "GUI");

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForFileProcessingAsync_ReturnsTrue_WhenFileDeleted()
    {
        var testFile = Path.Combine(_testDir, ".wait.test");
        File.WriteAllText(testFile, "test");
        _createdFiles.Add(testFile);

        // Delete the file after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            File.Delete(testFile);
        });

        var result = await _service.WaitForFileProcessingAsync(testFile, TimeSpan.FromSeconds(5));

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForFileProcessingAsync_ReturnsFalse_OnTimeout()
    {
        var testFile = Path.Combine(_testDir, ".timeout.test");
        File.WriteAllText(testFile, "test");
        _createdFiles.Add(testFile);

        var result = await _service.WaitForFileProcessingAsync(testFile, TimeSpan.FromMilliseconds(500));

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForFileProcessingAsync_ReturnsTrue_WhenFileNeverExisted()
    {
        var testFile = Path.Combine(_testDir, ".nonexistent.test");

        var result = await _service.WaitForFileProcessingAsync(testFile, TimeSpan.FromSeconds(1));

        Assert.True(result);
    }

    [Fact]
    public void EnsureGUIVisible_MethodExists()
    {
        // NOTE: We do NOT call EnsureGUIVisible because it launches cimistatus.exe
        // on a machine with Cimian installed. This would spawn a GUI window during tests.
        
        // Instead, verify the method exists and is callable via reflection
        var method = typeof(TriggerService).GetMethod("EnsureGUIVisible");
        
        Assert.NotNull(method);
        Assert.Equal("EnsureGUIVisible", method.Name);
        Assert.Equal(typeof(bool), method.ReturnType);
    }
}
